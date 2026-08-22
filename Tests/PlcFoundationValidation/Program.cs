using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MyPLCFoundationLibrary.Alarms;
using MyPLCFoundationLibrary.Historian;
using MyPLCFoundationLibrary.Logging;

namespace PlcFoundationValidation;

/// <summary>
/// 模拟一个 PLC 在约 70 秒内的运行过程，端到端验证 P0 基础库（日志/历史/报警/审计/诊断）。
/// 每个能力块都带自校验断言，并打印 PASS/FAIL 汇总；全 PASS 退出码 0，否则 1。
/// </summary>
internal sealed class FakeHealthSource : IConnectionHealthSource
{
    public int Total { get; init; }
    public int Connected { get; init; }
}

internal static class Program
{
    private static readonly List<(string Name, bool Pass, string Detail)> Results = new();

    private static void Check(string name, bool pass, string detail = "")
    {
        Results.Add((name, pass, detail));
        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  —  " + detail : "")}");
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("=== P0 基础库 端到端验证 ===");
        Console.WriteLine($"运行时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        var temp = Path.Combine(Path.GetTempPath(), "PlcValidation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            ValidateLogging(temp);
            ValidateHistorian(temp);
            ValidateAlarms(temp);
            ValidateAudit(temp);
            ValidateDiagnostics(temp);
        }
        catch (Exception ex)
        {
            Check("整体执行", false, "未捕获异常: " + ex);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* 忽略清理失败 */ }
        }

        Console.WriteLine();
        Console.WriteLine("=== 汇总 ===");
        var passCount = Results.Count(r => r.Pass);
        foreach (var r in Results)
            Console.WriteLine($"  {(r.Pass ? "✓" : "✗")} {r.Name}");
        Console.WriteLine($"通过 {passCount}/{Results.Count}");

        return Results.All(r => r.Pass) ? 0 : 1;
    }

    // 1) 结构化日志：初始化 → 写各级别 → 刷新 → 校验文件落盘
    private static void ValidateLogging(string temp)
    {
        var dir = Path.Combine(temp, "logs");
        StructuredLogger.Init(new LogConfig
        {
            LogDirectory = dir,
            AppName = "PlcValidation",
            FileNamePrefix = "validation",
            ConsoleEnabled = false,
            MinLevel = AppLogLevel.Debug,
            RetainedFileCountLimit = 7
        });

        var log = StructuredLogger.Current;
        log.Information("验证启动 Tag={Tag}", "DB1.DBW240");
        log.Warning("示例告警: 温度偏高");
        log.Error(new Exception("模拟通信故障"), "通信异常");

        StructuredLogger.CloseAndFlush();

        var files = Directory.GetFiles(dir, "validation-*").ToArray();
        var content = files.Length > 0 ? File.ReadAllText(files[0]) : string.Empty;
        var hasInfo = content.Contains("验证启动");
        var hasErr = content.Contains("通信异常");

        Check("日志: 文件按天滚动生成", files.Length > 0, files.Length > 0 ? Path.GetFileName(files[0]) : "无文件");
        Check("日志: 结构化内容落盘", hasInfo && hasErr, $"info={hasInfo}, err={hasErr}");
    }

    // 2) 过程历史库：插入原始采样 → 聚合 → 自校验 min/avg/max/last → 保留策略
    private static void ValidateHistorian(string temp)
    {
        var db = Path.Combine(temp, "historian.db");
        var cfg = new HistorianConfig
        {
            SamplingIntervalMs = 1000,
            AggregationIntervalMs = 60_000,
            AggregationEnabled = true,
            RetentionDays = 30
        };
        using var store = new HistorianStore(db, cfg);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const string tag = "DB1.DBW240";
        var series = new List<(long ts, double v, bool good)>();
        for (var i = 0; i < 70; i++)
        {
            var v = 100 + i * 2 + (i % 3);   // 爬坡序列
            var good = i % 10 != 0;           // 每第 10 个点质量为坏
            var ts = now + i * 1000;
            series.Add((ts, v, good));
            store.InsertRaw(tag, ts, v, good);
        }
        const string tag2 = "温度1";
        for (var i = 0; i < 65; i++)
            store.InsertRaw(tag2, now + i * 1000, 80 + (i % 5), true);

        store.AggregateAsync().GetAwaiter().GetResult();

        // 原始查询
        var raw = store.GetRaw(tag, now, now + 70_000);
        Check("历史库: 原始采样查询", raw.Count == 70, $"返回 {raw.Count} 点");

        // 聚合自校验：用相同算法重算期望，与 SQLite 聚合结果逐桶比对
        var bucketMs = 60_000L;
        var expected = series.Where(s => s.good)
            .GroupBy(s => (s.ts / bucketMs) * bucketMs)
            .ToDictionary(g => g.Key, g => (
                Min: g.Min(x => x.v),
                Max: g.Max(x => x.v),
                Avg: g.Average(x => x.v),
                Last: g.OrderByDescending(x => x.ts).First().v));

        var agg = store.GetAggregated(tag, now, now + 70_000, bucketMs);
        var aggOk = agg.Count == expected.Count;
        var aggDetail = $"桶数 {agg.Count}/{expected.Count}";
        if (aggOk)
        {
            foreach (var p in agg)
            {
                if (!expected.TryGetValue(p.BucketStart, out var e)) { aggOk = false; aggDetail = $"多余桶 {p.BucketStart}"; break; }
                if (Math.Abs(p.Min - e.Min) > 1e-9 || Math.Abs(p.Max - e.Max) > 1e-9 ||
                    Math.Abs(p.Avg - e.Avg) > 1e-6 || Math.Abs(p.Last - e.Last) > 1e-9)
                { aggOk = false; aggDetail = $"桶 {p.BucketStart} 数值不匹配"; break; }
            }
        }
        Check("历史库: 聚合 min/avg/max/last 正确", aggOk, aggDetail);

        // 保留策略：插入 5 天前（应保留）与 40 天前（应清理）的点
        store.InsertRaw(tag, now - 5L * 86_400_000, 1, true);
        store.InsertRaw(tag, now - 40L * 86_400_000, 1, true);
        store.ApplyRetentionAsync().GetAwaiter().GetResult();
        var after = store.GetRaw(tag, 0, now + 70_000);
        var oldGone = !after.Any(p => p.Ts < now - 35L * 86_400_000);
        Check("历史库: 保留期清理旧数据", oldGone, $"保留后点数 {after.Count}");
    }

    // 3) 报警持久化：触发/去重/确认/清除/未确认计数/历史查询
    private static void ValidateAlarms(string temp)
    {
        var db = Path.Combine(temp, "alarms.db");
        using var store = new AlarmStore(db);

        var a = store.Raise("温度1", AlarmSeverity.Critical, "超温 120°C");
        Check("报警: Raise 创建激活报警", a.Id > 0 && a.State == AlarmState.Active, $"id={a.Id}");

        var dup = store.Raise("温度1", AlarmSeverity.Critical, "超温(重复触发)");
        Check("报警: 同 Tag 去重", dup.Id == a.Id, $"dup id={dup.Id}");

        var ack = store.Acknowledge(a.Id, "operator1");
        var afterAck = store.GetById(a.Id)!;
        Check("报警: 确认并记录确认人", ack && afterAck.State == AlarmState.Acknowledged && afterAck.AckUser == "operator1",
            $"state={afterAck.State}, user={afterAck.AckUser}");

        var cleared = store.Clear(a.Id);
        var afterClear = store.GetById(a.Id)!;
        Check("报警: 清除", cleared && afterClear.State == AlarmState.Cleared, $"state={afterClear.State}");

        store.Raise("DB1.DBW240", AlarmSeverity.Warning, "位置偏差超限");
        Check("报警: 未确认计数", store.CountUnacknowledged() == 1, $"unack={store.CountUnacknowledged()}");

        var hist = store.GetHistory(limit: 50);
        Check("报警: 历史查询", hist.Count >= 2, $"历史 {hist.Count} 条");
    }

    // 4) 审计：记录关键操作并查询
    private static void ValidateAudit(string temp)
    {
        var db = Path.Combine(temp, "audit.db");
        using var store = new AuditStore(db);
        store.Record("admin", "Login");
        store.Record("operator1", "AcknowledgeAlarm", "温度1", "确认超温报警");
        store.Record("operator1", "WriteTag", "DB1.DBW250", "value=5");

        var list = store.Query(limit: 50);
        var ok = list.Any(r => r.Action == "Login" && r.User == "admin")
              && list.Any(r => r.Action == "AcknowledgeAlarm")
              && list.Any(r => r.Action == "WriteTag" && r.Target == "DB1.DBW250");
        Check("审计: 记录与查询", ok, $"记录 {list.Count} 条");
    }

    // 5) 诊断：CPU/内存采样 + 连接健康注入
    private static void ValidateDiagnostics(string temp)
    {
        using var diag = new DiagnosticsService(TimeSpan.FromMilliseconds(50));
        diag.RegisterHealthSource(new FakeHealthSource { Total = 2, Connected = 1 });
        var snap = diag.GetSnapshot();

        var cpuOk = snap.CpuPercent is >= 0 and <= 100;
        var memOk = snap.MemoryWorkingSetMb > 0;
        var connOk = snap.ConnectionsTotal == 2 && snap.ConnectionsConnected == 1;
        Check("诊断: CPU/内存采样", cpuOk && memOk, $"cpu={snap.CpuPercent}%, mem={snap.MemoryWorkingSetMb}MB, diskFree={snap.DiskFreeMb}MB");
        Check("诊断: 连接健康注入", connOk, $"total={snap.ConnectionsTotal}, connected={snap.ConnectionsConnected}");
    }
}
