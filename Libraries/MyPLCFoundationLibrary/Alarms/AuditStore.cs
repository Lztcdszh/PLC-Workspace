using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace MyPLCFoundationLibrary.Alarms;

/// <summary>一条操作员行为审计记录（满足 P0 审计要求：谁、在何时、对什么、做了什么、结果）。</summary>
public sealed class AuditRecord
{
    public long Id { get; set; }
    public DateTime Ts { get; set; }
    public string User { get; set; } = string.Empty;
    /// <summary>动作类型，例如 Login / Logout / AcknowledgeAlarm / WriteTag / SwitchMode / RecipeLoad。</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>操作目标，例如 标签名 / 报警Id / 配方名。</summary>
    public string Target { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool Success { get; set; } = true;
}

/// <summary>
/// 操作员行为审计存储（SQLite）。记录所有关键操作（登录、确认报警、写标签、切换模式、加载配方等），
/// 用于事故追溯与合规。仅追加，不修改不删除。
/// </summary>
public sealed class AuditStore : IDisposable
{
    private readonly string _dbPath;

    public AuditStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        EnsureTable();
    }

    public static string DefaultDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PLCTesting", "audit.db");

    private SqliteConnection Conn() => new($"Data Source={_dbPath}");

    private void EnsureTable()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS AuditLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Ts INTEGER NOT NULL,
                User TEXT NOT NULL DEFAULT '',
                Action TEXT NOT NULL DEFAULT '',
                Target TEXT NOT NULL DEFAULT '',
                Detail TEXT NOT NULL DEFAULT '',
                Success INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS IX_Audit_Ts ON AuditLog(Ts);
            CREATE INDEX IF NOT EXISTS IX_Audit_Action ON AuditLog(Action);
        ";
        cmd.ExecuteNonQuery();
    }

    private static long ToUnix(DateTime dt) => DateTimeOffset.FromUnixTimeSeconds(0)
        .AddTicks(dt.Ticks - DateTimeOffset.UnixEpoch.Ticks).ToUnixTimeMilliseconds();
    private static DateTime FromUnix(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    /// <summary>记录一条审计（仅追加）。</summary>
    public void Record(string user, string action, string? target = null, string? detail = null, bool success = true)
    {
        using var c = Conn();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO AuditLog (Ts, User, Action, Target, Detail, Success) VALUES (@Ts, @User, @Action, @Target, @Detail, @Success);";
        cmd.Parameters.AddWithValue("@Ts", ToUnix(DateTime.Now));
        cmd.Parameters.AddWithValue("@User", user ?? string.Empty);
        cmd.Parameters.AddWithValue("@Action", action ?? string.Empty);
        cmd.Parameters.AddWithValue("@Target", target ?? string.Empty);
        cmd.Parameters.AddWithValue("@Detail", detail ?? string.Empty);
        cmd.Parameters.AddWithValue("@Success", success ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>查询审计（按时间倒序）。</summary>
    public List<AuditRecord> Query(DateTime? from = null, DateTime? to = null, string? action = null, string? user = null, int limit = 500)
    {
        var list = new List<AuditRecord>();
        using var c = Conn();
        c.Open();
        var sql = "SELECT Id, Ts, User, Action, Target, Detail, Success FROM AuditLog WHERE 1=1";
        if (from.HasValue) sql += " AND Ts >= @From";
        if (to.HasValue) sql += " AND Ts <= @To";
        if (!string.IsNullOrEmpty(action)) sql += " AND Action = @Action";
        if (!string.IsNullOrEmpty(user)) sql += " AND User = @User";
        sql += " ORDER BY Ts DESC LIMIT @Limit;";
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (from.HasValue) cmd.Parameters.AddWithValue("@From", ToUnix(from.Value));
        if (to.HasValue) cmd.Parameters.AddWithValue("@To", ToUnix(to.Value));
        if (!string.IsNullOrEmpty(action)) cmd.Parameters.AddWithValue("@Action", action!);
        if (!string.IsNullOrEmpty(user)) cmd.Parameters.AddWithValue("@User", user!);
        cmd.Parameters.AddWithValue("@Limit", Math.Max(1, limit));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AuditRecord
            {
                Id = r.GetInt64(0),
                Ts = FromUnix(r.GetInt64(1)),
                User = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                Action = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                Target = r.IsDBNull(4) ? string.Empty : r.GetString(4),
                Detail = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                Success = r.GetInt32(6) == 1
            });
        return list;
    }

    public void Dispose() { GC.SuppressFinalize(this); }
}
