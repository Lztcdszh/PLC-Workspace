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
            => Count(new ChangeLogQuery { Address = address, From = from, To = to });

        public List<ChangeLogRecord> Query(string address = null, DateTime? from = null, DateTime? to = null, int pageSize = 100, int pageIndex = 0)
            => QueryPage(new ChangeLogQuery
            {
                Address = address,
                From = from,
                To = to,
                PageSize = pageSize,
                PageIndex = pageIndex,
            }).Items;

        /// <summary>按条件统计总条数。</summary>
        public int Count(ChangeLogQuery query)
        {
            query ??= new ChangeLogQuery();
            using var conn = CreateConnection();
            conn.Open();
            var cmd = conn.CreateCommand();
            var sql = "SELECT COUNT(*) FROM TagChangeLog WHERE 1=1";
            BuildWhere(cmd, ref sql, query);
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        /// <summary>按条件分页查询，返回总数与当前页数据（一次往返拿到 Total 与 Items）。</summary>
        public ChangeLogPage QueryPage(ChangeLogQuery query)
        {
            query ??= new ChangeLogQuery();
            var pageSize = Math.Max(1, query.PageSize);
            var pageIndex = Math.Max(0, query.PageIndex);

            using var conn = CreateConnection();
            conn.Open();

            // 总数
            var countCmd = conn.CreateCommand();
            var countSql = "SELECT COUNT(*) FROM TagChangeLog WHERE 1=1";
            BuildWhere(countCmd, ref countSql, query);
            countCmd.CommandText = countSql;
            var total = Convert.ToInt32(countCmd.ExecuteScalar());

            // 当前页
            var cmd = conn.CreateCommand();
            var sql = "SELECT Id, TimeStamp, TagAddress, TagName, OldValue, NewValue, DataType, Description FROM TagChangeLog WHERE 1=1";
            BuildWhere(cmd, ref sql, query);
            sql += query.Ascending
                ? " ORDER BY TimeStamp ASC, Id ASC LIMIT @PageSize OFFSET @Offset"
                : " ORDER BY TimeStamp DESC, Id DESC LIMIT @PageSize OFFSET @Offset";
            cmd.Parameters.AddWithValue("@PageSize", pageSize);
            cmd.Parameters.AddWithValue("@Offset", (long)pageIndex * pageSize);
            cmd.CommandText = sql;

            var page = new ChangeLogPage { Total = total };
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                page.Items.Add(new ChangeLogRecord
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
            return page;
        }

        /// <summary>按条件取出全部匹配记录（不分页），用于导出。</summary>
        public List<ChangeLogRecord> QueryAll(ChangeLogQuery query)
        {
            query ??= new ChangeLogQuery();
            using var conn = CreateConnection();
            conn.Open();
            var cmd = conn.CreateCommand();
            var sql = "SELECT Id, TimeStamp, TagAddress, TagName, OldValue, NewValue, DataType, Description FROM TagChangeLog WHERE 1=1";
            BuildWhere(cmd, ref sql, query);
            sql += query.Ascending ? " ORDER BY TimeStamp ASC, Id ASC" : " ORDER BY TimeStamp DESC, Id DESC";
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

        /// <summary>
        /// 列出库里出现过的所有地址，附标签名与条数。
        /// 地址下拉改为从这里动态生成 —— 原来 XAML/VM 里硬编码了几个地址，
        /// 新增的数据点在下拉里看不到，用户会以为"没有数据"。
        /// </summary>
        public List<AddressStat> GetAddressStats()
        {
            using var conn = CreateConnection();
            conn.Open();
            var cmd = conn.CreateCommand();
            // 取每个地址的条数 + 最近一条的标签名（TagName 在历史数据里可能被写成地址本身）
            cmd.CommandText = @"
                SELECT t.TagAddress, t.TagName, s.Cnt
                FROM (
                    SELECT TagAddress, MAX(Id) AS MaxId, COUNT(*) AS Cnt
                    FROM TagChangeLog
                    GROUP BY TagAddress
                ) s
                JOIN TagChangeLog t ON t.Id = s.MaxId
                ORDER BY s.Cnt DESC, t.TagAddress ASC;
            ";
            var list = new List<AddressStat>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new AddressStat
                {
                    Address = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    TagName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    Count = reader.GetInt32(2)
                });
            }
            return list;
        }

        /// <summary>库里的总记录数（不区分条件），用于页面提示。</summary>
        public int CountAll()
        {
            using var conn = CreateConnection();
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM TagChangeLog";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        /// <summary>统一拼接 WHERE 条件（参数化，防注入）。</summary>
        private static void BuildWhere(SqliteCommand cmd, ref string sql, ChangeLogQuery q)
        {
            if (!string.IsNullOrWhiteSpace(q.Address))
            {
                sql += " AND TagAddress = @TagAddress";
                cmd.Parameters.AddWithValue("@TagAddress", q.Address.Trim());
            }
            if (q.From.HasValue)
            {
                sql += " AND TimeStamp >= @From";
                cmd.Parameters.AddWithValue("@From", q.From.Value.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            }
            if (q.To.HasValue)
            {
                sql += " AND TimeStamp <= @To";
                cmd.Parameters.AddWithValue("@To", q.To.Value.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            }
            if (q.ChangedOnly)
            {
                // 旧值为空或与旧值不等 → 真正发生了变化。
                // 库里存在 0→0 这种记录（TagValueChanged 在值未变时也可能触发），必须能过滤掉。
                sql += " AND IFNULL(OldValue,'') <> NewValue";
            }
            if (!string.IsNullOrWhiteSpace(q.Keyword))
            {
                sql += @" AND (TagName LIKE @Kw ESCAPE '\'
                             OR TagAddress LIKE @Kw ESCAPE '\'
                             OR IFNULL(OldValue,'') LIKE @Kw ESCAPE '\'
                             OR NewValue LIKE @Kw ESCAPE '\'
                             OR IFNULL(Description,'') LIKE @Kw ESCAPE '\')";
                cmd.Parameters.AddWithValue("@Kw", "%" + EscapeLike(q.Keyword.Trim()) + "%");
            }
        }

        /// <summary>转义 LIKE 的通配符，避免用户输入的 % _ \ 被当成模式。</summary>
        private static string EscapeLike(string s) =>
            s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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
