using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MyPLCFoundationLibrary.Historian;

/// <summary>
/// 过程历史库（Process Historian）：基于 SQLite。
/// <list type="bullet">
///   <item>原始表 RawSamples：周期采样落盘（支持长期趋势回放）。</item>
///   <item>聚合表 AggSamples：按桶压缩（min/avg/max/last/count），支撑低分辨率回放与快速统计。</item>
///   <item>保留策略：超过 RetentionDays 的原始数据自动清理，聚合数据长期保留。</item>
/// </list>
/// 时间统一用 Unix 毫秒（long）存储，便于范围查询与桶运算。
/// 可独立使用（直接 InsertRaw），也可 Start 后由采样器自动写入。
/// </summary>
public sealed class HistorianStore : IDisposable
{
    private readonly string _dbPath;
    private readonly HistorianConfig _config;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Func<CancellationToken, Task<IReadOnlyList<SampleInput>>>? _sampler;

    public HistorianStore(string dbPath, HistorianConfig config)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        EnsureTables();
    }

    /// <summary>默认数据库路径：%LocalAppData%/PLCTesting/historian.db。</summary>
    public static string DefaultDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PLCTesting", "historian.db");

    private SqliteConnection Conn() => new($"Data Source={_dbPath}");

    private void EnsureTables()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS RawSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Tag TEXT NOT NULL,
                Ts INTEGER NOT NULL,
                Value REAL NOT NULL,
                Quality INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Raw_TagTs ON RawSamples(Tag, Ts);
            CREATE TABLE IF NOT EXISTS AggSamples (
                Tag TEXT NOT NULL,
                BucketMs INTEGER NOT NULL,
                BucketStart INTEGER NOT NULL,
                Min REAL NOT NULL,
                Max REAL NOT NULL,
                Avg REAL NOT NULL,
                Last REAL NOT NULL,
                Count INTEGER NOT NULL,
                PRIMARY KEY (Tag, BucketMs, BucketStart)
            );
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 启动自动采样与聚合。sampler 在每次采样时被调用以取得当前标签值。
    /// </summary>
    public void Start(Func<CancellationToken, Task<IReadOnlyList<SampleInput>>> sampler)
    {
        if (sampler == null) throw new ArgumentNullException(nameof(sampler));
        lock (_gate)
        {
            if (_cts != null) return; // 已在运行
            _sampler = sampler;
            _cts = new CancellationTokenSource();
            _ = SampleLoopAsync(_cts.Token);
            if (_config.AggregationEnabled)
                _ = AggLoopAsync(_cts.Token);
        }
    }

    /// <summary>停止自动采样与聚合。</summary>
    public void Stop()
    {
        CancellationTokenSource? cts = null;
        lock (_gate) { cts = _cts; _cts = null; _sampler = null; }
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private async Task SampleLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.SamplingIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    var samples = await _sampler!(token);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var s in samples)
                        if (!string.IsNullOrEmpty(s.Tag))
                            InsertRaw(s.Tag, now, s.Value, s.Good);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Historian] 采样失败: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task AggLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_config.AggregationIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try { await AggregateAsync(); await ApplyRetentionAsync(); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Historian] 聚合/保留失败: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>插入一条原始采样（也可由外部直接调用，例如导入历史数据）。</summary>
    public void InsertRaw(string tag, long ts, double value, bool good)
    {
        if (string.IsNullOrEmpty(tag)) return;
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO RawSamples (Tag, Ts, Value, Quality) VALUES (@Tag, @Ts, @Value, @Quality);";
        cmd.Parameters.AddWithValue("@Tag", tag);
        cmd.Parameters.AddWithValue("@Ts", ts);
        cmd.Parameters.AddWithValue("@Value", value);
        cmd.Parameters.AddWithValue("@Quality", good ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 将原始数据按桶聚合写入 AggSamples（基于 Quality=1 的好点）。
    /// 对未存在桶为插入、已存在桶为覆盖（每次重算，幂等）。
    /// </summary>
    public Task AggregateAsync()
    {
        return Task.Run(() =>
        {
            var bucketMs = _config.AggregationIntervalMs;
            using var c = Conn();
            c.Open();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO AggSamples (Tag, BucketMs, BucketStart, Min, Max, Avg, Last, Count)
                SELECT Tag,
                       @BucketMs,
                       (Ts / @BucketMs) * @BucketMs AS BucketStart,
                       MIN(Value) AS Min,
                       MAX(Value) AS Max,
                       AVG(Value) AS Avg,
                       MAX(CASE WHEN rn = 1 THEN Value END) AS LastVal,
                       COUNT(*) AS Cnt
                FROM (
                    SELECT Tag, Ts, Value,
                           ROW_NUMBER() OVER (PARTITION BY Tag, (Ts / @BucketMs) * @BucketMs ORDER BY Ts DESC) AS rn
                    FROM RawSamples
                    WHERE Quality = 1
                )
                GROUP BY Tag, (Ts / @BucketMs) * @BucketMs
                ON CONFLICT(Tag, BucketMs, BucketStart) DO UPDATE SET
                    Min = excluded.Min,
                    Max = excluded.Max,
                    Avg = excluded.Avg,
                    Last = excluded.Last,
                    Count = excluded.Count;
            ";
            cmd.Parameters.AddWithValue("@BucketMs", bucketMs);
            cmd.ExecuteNonQuery();
            tx.Commit();
        });
    }

    /// <summary>清理超过保留期的原始数据（聚合数据保留）。</summary>
    public Task ApplyRetentionAsync()
    {
        return Task.Run(() =>
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)_config.RetentionDays * 86_400_000L;
            using var c = Conn();
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM RawSamples WHERE Ts < @Cutoff;";
            cmd.Parameters.AddWithValue("@Cutoff", cutoff);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>查询原始采样点（按时间升序）。</summary>
    public List<HistorianRawPoint> GetRaw(string tag, long fromTs, long toTs)
    {
        var list = new List<HistorianRawPoint>();
        if (string.IsNullOrEmpty(tag)) return list;
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Ts, Value, Quality FROM RawSamples WHERE Tag=@Tag AND Ts BETWEEN @From AND @To ORDER BY Ts ASC;";
        cmd.Parameters.AddWithValue("@Tag", tag);
        cmd.Parameters.AddWithValue("@From", fromTs);
        cmd.Parameters.AddWithValue("@To", toTs);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new HistorianRawPoint
            {
                Ts = r.GetInt64(0),
                Value = r.GetDouble(1),
                Good = r.GetInt32(2) == 1
            });
        return list;
    }

    /// <summary>查询聚合点（按桶起始时间升序）。</summary>
    public List<HistorianAggPoint> GetAggregated(string tag, long fromTs, long toTs, long bucketMs)
    {
        var list = new List<HistorianAggPoint>();
        if (string.IsNullOrEmpty(tag) || bucketMs <= 0) return list;
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        // 下界减一个桶宽：桶起始被下取整到桶边界，可能早于查询起点 from，
        // 但该桶实际覆盖了 [from, to] 区间的一部分，应被包含。
        cmd.CommandText = "SELECT BucketStart, Min, Max, Avg, Last, Count FROM AggSamples WHERE Tag=@Tag AND BucketMs=@BucketMs AND BucketStart BETWEEN @From - @BucketMs AND @To ORDER BY BucketStart ASC;";
        cmd.Parameters.AddWithValue("@Tag", tag);
        cmd.Parameters.AddWithValue("@BucketMs", bucketMs);
        cmd.Parameters.AddWithValue("@From", fromTs);
        cmd.Parameters.AddWithValue("@To", toTs);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new HistorianAggPoint
            {
                BucketStart = r.GetInt64(0),
                Min = r.GetDouble(1),
                Max = r.GetDouble(2),
                Avg = r.GetDouble(3),
                Last = r.GetDouble(4),
                Count = r.GetInt32(5)
            });
        return list;
    }

    public void Dispose() => Stop();
}
