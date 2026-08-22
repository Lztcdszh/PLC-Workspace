using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace MyPLCFoundationLibrary.Alarms;

/// <summary>
/// 报警持久化存储（SQLite）。满足 P0 要求：报警可追溯、可确认审计、历史可查。
/// 状态机：Raise →（Active）→ Acknowledge →（Acknowledged）→ Clear →（Cleared）。
/// 同一 Tag 的未清除报警视为同一条，重复 Raise 仅刷新消息/时间，避免刷屏。
/// </summary>
public sealed class AlarmStore : IDisposable
{
    private readonly string _dbPath;

    public AlarmStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        EnsureTable();
    }

    public static string DefaultDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PLCTesting", "alarms.db");

    private SqliteConnection Conn() => new($"Data Source={_dbPath}");

    private void EnsureTable()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Alarms (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Tag TEXT NOT NULL,
                Severity INTEGER NOT NULL,
                Message TEXT NOT NULL DEFAULT '',
                ActiveTs INTEGER NOT NULL,
                AckTs INTEGER NULL,
                AckUser TEXT NOT NULL DEFAULT '',
                ClearTs INTEGER NULL,
                State INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Alarms_Tag ON Alarms(Tag);
            CREATE INDEX IF NOT EXISTS IX_Alarms_ActiveTs ON Alarms(ActiveTs);
        ";
        cmd.ExecuteNonQuery();
    }

    private static long ToUnix(DateTime dt) => DateTimeOffset.FromUnixTimeSeconds(0)
        .AddTicks(dt.Ticks - DateTimeOffset.UnixEpoch.Ticks).ToUnixTimeMilliseconds();
    private static DateTime FromUnix(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    /// <summary>
    /// 触发一条报警。若该 Tag 已有未清除报警，则刷新其消息与激活时间并返回原记录；
    /// 否则插入新记录（状态 Active）。
    /// </summary>
    public AlarmRecord Raise(string tag, AlarmSeverity severity, string message)
    {
        if (string.IsNullOrEmpty(tag)) throw new ArgumentNullException(nameof(tag));
        var existing = GetActiveByTag(tag);
        if (existing != null)
        {
            existing.Message = message;
            existing.ActiveTs = DateTime.Now;
            existing.Severity = severity;
            Update(existing);
            return existing;
        }
        var rec = new AlarmRecord
        {
            Tag = tag,
            Severity = severity,
            Message = message,
            ActiveTs = DateTime.Now,
            State = AlarmState.Active
        };
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Alarms (Tag, Severity, Message, ActiveTs, AckTs, AckUser, ClearTs, State)
            VALUES (@Tag, @Sev, @Msg, @Active, NULL, '', NULL, @State);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@Tag", rec.Tag);
        cmd.Parameters.AddWithValue("@Sev", (int)rec.Severity);
        cmd.Parameters.AddWithValue("@Msg", rec.Message);
        cmd.Parameters.AddWithValue("@Active", ToUnix(rec.ActiveTs));
        cmd.Parameters.AddWithValue("@State", (int)rec.State);
        rec.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return rec;
    }

    /// <summary>确认一条报警（记录确认人，状态 → Acknowledged）。</summary>
    public bool Acknowledge(long id, string user)
    {
        var rec = GetById(id);
        if (rec == null || rec.State == AlarmState.Cleared) return false;
        rec.AckTs = DateTime.Now;
        rec.AckUser = user ?? string.Empty;
        rec.State = AlarmState.Acknowledged;
        Update(rec);
        return true;
    }

    /// <summary>确认某 Tag 下全部未确认报警。</summary>
    public int AcknowledgeAllByTag(string tag, string user)
    {
        int n = 0;
        foreach (var a in GetActive())
            if (a.Tag == tag && a.State == AlarmState.Active)
                if (Acknowledge(a.Id, user)) n++;
        return n;
    }

    /// <summary>清除一条报警（状态 → Cleared）。</summary>
    public bool Clear(long id)
    {
        var rec = GetById(id);
        if (rec == null || rec.State == AlarmState.Cleared) return false;
        rec.ClearTs = DateTime.Now;
        rec.State = AlarmState.Cleared;
        Update(rec);
        return true;
    }

    public AlarmRecord? GetById(long id)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Tag, Severity, Message, ActiveTs, AckTs, AckUser, ClearTs, State FROM Alarms WHERE Id=@Id;";
        cmd.Parameters.AddWithValue("@Id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>获取某 Tag 当前未清除的报警（用于去重）。</summary>
    public AlarmRecord? GetActiveByTag(string tag)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Tag, Severity, Message, ActiveTs, AckTs, AckUser, ClearTs, State FROM Alarms WHERE Tag=@Tag AND State <> @Cleared ORDER BY ActiveTs DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("@Tag", tag);
        cmd.Parameters.AddWithValue("@Cleared", (int)AlarmState.Cleared);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>获取所有未清除报警（Active + Acknowledged）。</summary>
    public List<AlarmRecord> GetActive()
    {
        var list = new List<AlarmRecord>();
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Tag, Severity, Message, ActiveTs, AckTs, AckUser, ClearTs, State FROM Alarms WHERE State <> @Cleared ORDER BY ActiveTs DESC;";
        cmd.Parameters.AddWithValue("@Cleared", (int)AlarmState.Cleared);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>历史查询（按时间倒序）。</summary>
    public List<AlarmRecord> GetHistory(DateTime? from = null, DateTime? to = null, string? tag = null, int limit = 500)
    {
        var list = new List<AlarmRecord>();
        using var c = Conn();
        c.Open();
        var sql = "SELECT Id, Tag, Severity, Message, ActiveTs, AckTs, AckUser, ClearTs, State FROM Alarms WHERE 1=1";
        if (from.HasValue) { sql += " AND ActiveTs >= @From"; }
        if (to.HasValue) { sql += " AND ActiveTs <= @To"; }
        if (!string.IsNullOrEmpty(tag)) { sql += " AND Tag = @Tag"; }
        sql += " ORDER BY ActiveTs DESC LIMIT @Limit;";
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (from.HasValue) cmd.Parameters.AddWithValue("@From", ToUnix(from.Value));
        if (to.HasValue) cmd.Parameters.AddWithValue("@To", ToUnix(to.Value));
        if (!string.IsNullOrEmpty(tag)) cmd.Parameters.AddWithValue("@Tag", tag!);
        cmd.Parameters.AddWithValue("@Limit", Math.Max(1, limit));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>当前未确认（Active 状态）报警数量，用于状态栏角标。</summary>
    public int CountUnacknowledged() => CountByState(AlarmState.Active);

    public int CountActive() => CountByState(AlarmState.Active) + CountByState(AlarmState.Acknowledged);

    private int CountByState(AlarmState state)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Alarms WHERE State=@State;";
        cmd.Parameters.AddWithValue("@State", (int)state);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Update(AlarmRecord rec)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Alarms SET Tag=@Tag, Severity=@Sev, Message=@Msg, ActiveTs=@Active, AckTs=@Ack, AckUser=@User, ClearTs=@Clear, State=@State WHERE Id=@Id;";
        cmd.Parameters.AddWithValue("@Tag", rec.Tag);
        cmd.Parameters.AddWithValue("@Sev", (int)rec.Severity);
        cmd.Parameters.AddWithValue("@Msg", rec.Message);
        cmd.Parameters.AddWithValue("@Active", ToUnix(rec.ActiveTs));
        cmd.Parameters.AddWithValue("@Ack", rec.AckTs.HasValue ? (object)ToUnix(rec.AckTs.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("@User", rec.AckUser);
        cmd.Parameters.AddWithValue("@Clear", rec.ClearTs.HasValue ? (object)ToUnix(rec.ClearTs.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("@State", (int)rec.State);
        cmd.Parameters.AddWithValue("@Id", rec.Id);
        cmd.ExecuteNonQuery();
    }

    private static AlarmRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Tag = r.GetString(1),
        Severity = (AlarmSeverity)r.GetInt32(2),
        Message = r.IsDBNull(3) ? string.Empty : r.GetString(3),
        ActiveTs = FromUnix(r.GetInt64(4)),
        AckTs = r.IsDBNull(5) ? null : FromUnix(r.GetInt64(5)),
        AckUser = r.IsDBNull(6) ? string.Empty : r.GetString(6),
        ClearTs = r.IsDBNull(7) ? null : FromUnix(r.GetInt64(7)),
        State = (AlarmState)r.GetInt32(8)
    };

    public void Dispose() { /* 连接按需创建，无需持久资源 */ GC.SuppressFinalize(this); }
}
