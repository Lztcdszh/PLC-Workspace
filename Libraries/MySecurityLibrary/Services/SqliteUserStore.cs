using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using MySecurityLibrary.Models;

namespace MySecurityLibrary.Services;

/// <summary>
/// 基于 SQLite 的用户仓储实现。自包含、可移植：
/// 只要项目引用 Microsoft.Data.Sqlite 即可直接使用，不依赖任何业务库。
/// 默认管理员种子账号为 admin / admin123。
/// </summary>
public class SqliteUserStore : IUserStore
{
    private readonly string _dbPath;
    private readonly IPasswordHasher _hasher;

    /// <summary>默认数据库路径：%LocalAppData%/PLCTesting/users.db。（保持与原 PLCTesting 兼容）</summary>
    public static string DefaultDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PLCTesting", "users.db");

    public SqliteUserStore() : this(DefaultDbPath, new Pbkdf2PasswordHasher())
    {
    }

    public SqliteUserStore(string dbPath, IPasswordHasher hasher)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        EnsureTable();
    }

    private SqliteConnection CreateConnection() => new SqliteConnection($"Data Source={_dbPath}");

    private void EnsureTable()
    {
        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Users (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                UserName     TEXT NOT NULL UNIQUE,
                DisplayName  TEXT NOT NULL,
                Role         INTEGER NOT NULL,
                PasswordHash TEXT NOT NULL,
                PasswordSalt TEXT NOT NULL,
                IsEnabled    INTEGER NOT NULL,
                CreatedAt    TEXT NOT NULL,
                LastLoginAt  TEXT,
                CreatedBy    TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Users_UserName ON Users(UserName);
        ";
        cmd.ExecuteNonQuery();
    }

    public void EnsureSeeded()
    {
        if (Count() > 0) return;
        Create("admin", "管理员", UserRole.Admin, "admin123", "system");
    }

    public int Count()
    {
        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Users";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<AppUser> GetAll()
    {
        var list = new List<AppUser>();
        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, UserName, DisplayName, Role, PasswordHash, PasswordSalt,
                   IsEnabled, CreatedAt, LastLoginAt, CreatedBy
            FROM Users ORDER BY Id ASC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(ReadUser(reader));
        return list;
    }

    public AppUser? GetByUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, UserName, DisplayName, Role, PasswordHash, PasswordSalt,
                   IsEnabled, CreatedAt, LastLoginAt, CreatedBy
            FROM Users WHERE UserName = @UserName LIMIT 1";
        cmd.Parameters.AddWithValue("@UserName", userName.Trim());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadUser(reader) : null;
    }

    public AppUser Create(string userName, string displayName, UserRole role, string password, string createdBy)
    {
        if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("用户名不能为空", nameof(userName));
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("密码不能为空", nameof(password));
        if (GetByUserName(userName) != null) throw new InvalidOperationException($"用户名「{userName}」已存在");

        _hasher.Hash(password, out var salt, out var hash);
        var user = new AppUser
        {
            UserName = userName.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName.Trim() : displayName.Trim(),
            Role = role,
            PasswordHash = hash,
            PasswordSalt = salt,
            IsEnabled = true,
            CreatedAt = DateTime.Now,
            CreatedBy = createdBy ?? "system"
        };

        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Users (UserName, DisplayName, Role, PasswordHash, PasswordSalt, IsEnabled, CreatedAt, CreatedBy)
            VALUES (@UserName, @DisplayName, @Role, @PasswordHash, @PasswordSalt, 1, @CreatedAt, @CreatedBy);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@UserName", user.UserName);
        cmd.Parameters.AddWithValue("@DisplayName", user.DisplayName);
        cmd.Parameters.AddWithValue("@Role", (int)user.Role);
        cmd.Parameters.AddWithValue("@PasswordHash", user.PasswordHash);
        cmd.Parameters.AddWithValue("@PasswordSalt", user.PasswordSalt);
        cmd.Parameters.AddWithValue("@CreatedAt", user.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@CreatedBy", user.CreatedBy);
        user.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return user;
    }

    public void UpdateProfile(int id, string displayName, UserRole role, bool isEnabled)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Users SET DisplayName=@DisplayName, Role=@Role, IsEnabled=@IsEnabled WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@DisplayName", displayName?.Trim() ?? "");
        cmd.Parameters.AddWithValue("@Role", (int)role);
        cmd.Parameters.AddWithValue("@IsEnabled", isEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetPassword(int id, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword)) throw new ArgumentException("新密码不能为空", nameof(newPassword));
        _hasher.Hash(newPassword, out var salt, out var hash);
        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET PasswordHash=@Hash, PasswordSalt=@Salt WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Hash", hash);
        cmd.Parameters.AddWithValue("@Salt", salt);
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateLastLogin(int id, DateTime at)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Users SET LastLoginAt=@At WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@At", at.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var conn = CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Users WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    private static AppUser ReadUser(SqliteDataReader reader)
    {
        return new AppUser
        {
            Id = reader.GetInt32(0),
            UserName = reader.GetString(1),
            DisplayName = reader.GetString(2),
            Role = (UserRole)reader.GetInt32(3),
            PasswordHash = reader.GetString(4),
            PasswordSalt = reader.GetString(5),
            IsEnabled = reader.GetInt32(6) != 0,
            CreatedAt = DateTime.Parse(reader.GetString(7)),
            LastLoginAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)),
            CreatedBy = reader.IsDBNull(9) ? "" : reader.GetString(9)
        };
    }
}
