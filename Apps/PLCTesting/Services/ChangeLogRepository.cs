using Microsoft.Data.Sqlite;
using PLCTesting.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PLCTesting.Services
{
    /// <summary>
    /// SQLite 变化日志仓储：负责建表、写入、查询、测试数据生成。
    /// 所有数据库访问均为同步（WPF UI 线程调用），生产场景如需高频可改异步。
    /// 实现 IChangeLogSink，使数据接入层与具体存储解耦。
    /// </summary>
    public class ChangeLogRepository : IChangeLogSink
    {
        private readonly string _dbPath;

        public ChangeLogRepository(string dbPath)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            EnsureTable();
        }

        public static string DefaultDbPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PLCTesting", "plclog.db");

        private SqliteConnection CreateConnection() => new SqliteConnection($"Data Source={_dbPath}");

        private void EnsureTable()
        {
            using var conn = CreateConnection();
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS TagChangeLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TimeStamp TEXT NOT NULL,
                    TagAddress TEXT NOT NULL,
                    TagName TEXT NOT NULL,
                    OldValue TEXT,
                    NewValue TEXT NOT NULL,
                    DataType TEXT NOT NULL,
                    Description TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS IX_TagChangeLog_TimeStamp ON TagChangeLog(TimeStamp);
                CREATE INDEX IF NOT EXISTS IX_TagChangeLog_TagAddress ON TagChangeLog(TagAddress);
            ";
            cmd.ExecuteNonQuery();

            // 兼容旧库：补充 Description 列（已存在则忽略错误）
            try
            {
                using var alter = CreateConnection();
                alter.Open();
                using var acmd = alter.CreateCommand();
                acmd.CommandText = "ALTER TABLE TagChangeLog ADD COLUMN Description TEXT NOT NULL DEFAULT ''";
                acmd.ExecuteNonQuery();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // 列已存在，忽略
            }
        }

        /// <summary>IChangeLogSink 实现：直接委托给 Insert。</summary>
        public void Record(ChangeLogRecord record) => Insert(record);

        public void Insert(ChangeLogRecord record)
        {
            if (record == null) return;
            using var conn = CreateConnection();
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO TagChangeLog (TimeStamp, TagAddress, TagName, OldValue, NewValue, DataType, Description)
                VALUES (@TimeStamp, @TagAddress, @TagName, @OldValue, @NewValue, @DataType, @Description);
            ";
            cmd.Parameters.AddWithValue("@TimeStamp", record.TimeStamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            cmd.Parameters.AddWithValue("@TagAddress", record.TagAddress ?? string.Empty);
            cmd.Parameters.AddWithValue("@TagName", record.TagName ?? string.Empty);
            cmd.Parameters.AddWithValue("@OldValue", record.OldValue ?? string.Empty);
            cmd.Parameters.AddWithValue("@NewValue", record.NewValue ?? string.Empty);
            cmd.Parameters.AddWithValue("@DataType", record.DataType ?? string.Empty);
            cmd.Parameters.AddWithValue("@Description", record.Description ?? string.Empty);
            cmd.ExecuteNonQuery();
        }

        public int Count(string address = null, DateTime? from = null, DateTime? to = null)
        {
            using var conn = CreateConnection();
            conn.Open();
            var cmd = conn.CreateCommand();
            var sql = "SELECT COUNT(*) FROM TagChangeLog WHERE 1=1";
            AddFilters(cmd, ref sql, address, from, to);
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        public List<ChangeLogRecord> Query(string address = null, DateTime? from = null, DateTime? to = null, int pageSize = 100, int pageIndex = 0)
        {
            using var conn = CreateConnection();
            conn.Open();
            var cmd = conn.CreateCommand();
            var sql = "SELECT Id, TimeStamp, TagAddress, TagName, OldValue, NewValue, DataType, Description FROM TagChangeLog WHERE 1=1";
            AddFilters(cmd, ref sql, address, from, to);
            sql += " ORDER BY TimeStamp DESC, Id DESC LIMIT @PageSize OFFSET @Offset";
            cmd.Parameters.AddWithValue("@PageSize", Math.Max(1, pageSize));
            cmd.Parameters.AddWithValue("@Offset", Math.Max(0, pageIndex) * Math.Max(1, pageSize));
            cmd.CommandText = sql;

            var list = new List<ChangeLogRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ChangeLogRecord
                {
                    Id = reader.GetInt64(0),
                    TimeStamp = reader.GetDateTime(1),
                    TagAddress = reader.GetString(2),
                    TagName = reader.GetString(3),
                    OldValue = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    NewValue = reader.GetString(5),
                    DataType = reader.GetString(6),
                    Description = reader.IsDBNull(7) ? string.Empty : reader.GetString(7)
                });
            }
            return list;
        }

        private static void AddFilters(SqliteCommand cmd, ref string sql, string address, DateTime? from, DateTime? to)
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                sql += " AND TagAddress = @TagAddress";
                cmd.Parameters.AddWithValue("@TagAddress", address);
            }
            if (from.HasValue)
            {
                sql += " AND TimeStamp >= @From";
                cmd.Parameters.AddWithValue("@From", from.Value.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            }
            if (to.HasValue)
            {
                sql += " AND TimeStamp <= @To";
                cmd.Parameters.AddWithValue("@To", to.Value.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            }
        }

        public void DeleteAll()
        {
            using var conn = CreateConnection();
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM TagChangeLog";
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 插入几条演示数据，方便在无 PLC 环境下验证查询页面。
        /// </summary>
        public void SeedDemoData()
        {
            var now = DateTime.Now;
            var random = new Random();
            var records = new[]
            {
                new { Addr = "DB1.DBW230", Type = "Int16", Name = "小车状态", Desc = "小车运行状态字" },
                new { Addr = "DB1.DBB172", Type = "UInt8", Name = "小车故障代码", Desc = "小车故障代码" }
            };

            for (int i = 30; i >= 0; i--)
            {
                var r = records[random.Next(records.Length)];
                Insert(new ChangeLogRecord
                {
                    TimeStamp = now.AddSeconds(-i * 7),
                    TagAddress = r.Addr,
                    TagName = r.Name,
                    DataType = r.Type,
                    Description = r.Desc,
                    OldValue = random.Next(0, 10).ToString(),
                    NewValue = random.Next(0, 10).ToString()
                });
            }
        }
    }
}
