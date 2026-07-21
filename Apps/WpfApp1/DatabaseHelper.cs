using Kitware.VTK;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace WpfApp1
{
    /// <summary>
    /// 点云数据库记录（元数据视图）
    /// </summary>
    public class CloudRecord
    {
        public int Id;
        public string Name = "";
        public string Format = "PLY";
        public string SourcePath = "";
        public long PointCount;
        public double VolumeVoxel;
        public double VolumeConvex;
        public double BXmin, BXmax, BYmin, BYmax, BZmin, BZmax;
        public string CreatedAt = "";
    }

    /// <summary>
    /// SQLite 数据库帮助类：持久化点云元数据，并附带 PLY 数据副本以保证可重载
    /// </summary>
    public static class DatabaseHelper
    {
        private static string DbPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pointclouds.db");
        public static string StoreDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SavedClouds");

        public static void Init()
        {
            Directory.CreateDirectory(StoreDir);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Clouds (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT,
                    Format TEXT,
                    SourcePath TEXT,
                    PointCount INTEGER,
                    VolumeVoxel REAL,
                    VolumeConvex REAL,
                    BXmin REAL, BXmax REAL,
                    BYmin REAL, BYmax REAL,
                    BZmin REAL, BZmax REAL,
                    CreatedAt TEXT
                );";
            cmd.ExecuteNonQuery();
        }

        private static SqliteConnection Open()
        {
            var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            return conn;
        }

        /// <summary>
        /// 导出点云为 PLY 文件（二进制），返回是否成功
        /// </summary>
        public static bool ExportPly(vtkPolyData poly, string path)
        {
            try
            {
                var writer = vtkPLYWriter.New();
                writer.SetFileName(path);
                writer.SetInputData(poly);
                writer.SetFileTypeToBinary();
                writer.Write();
                var fi = new FileInfo(path);
                return fi.Exists && fi.Length > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] 导出PLY失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从文件重载点云（仅支持 PLY，入库时统一存为 PLY）
        /// </summary>
        public static vtkPolyData? LoadPly(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var reader = vtkPLYReader.New();
                reader.SetFileName(path);
                reader.Update();
                var poly = reader.GetOutput();
                if (poly == null || poly.GetNumberOfPoints() == 0) return null;
                return poly;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] 载入PLY失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 将一条点云入库：写入 PLY 副本并插入元数据记录。
        /// 返回数据库 Id；失败返回 0。
        /// </summary>
        public static int Insert(PointCloudItem item)
        {
            try
            {
                int newId;
                using (var conn = Open())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO Clouds (Name, Format, SourcePath, PointCount, VolumeVoxel, VolumeConvex, BXmin, BXmax, BYmin, BYmax, BZmin, BZmax, CreatedAt)
                        VALUES ($name, 'PLY', '', $cnt, $vv, $vc, $x0,$x1,$y0,$y1,$z0,$z1, $ts);
                        SELECT last_insert_rowid();";
                    cmd.Parameters.AddWithValue("$name", item.Name);
                    cmd.Parameters.AddWithValue("$cnt", item.PointCount);
                    cmd.Parameters.AddWithValue("$vv", item.VolumeComputed ? item.VoxelVolume : 0.0);
                    cmd.Parameters.AddWithValue("$vc", item.VolumeComputed ? item.ConvexVolume : 0.0);
                    cmd.Parameters.AddWithValue("$x0", item.Bounds[0]);
                    cmd.Parameters.AddWithValue("$x1", item.Bounds[1]);
                    cmd.Parameters.AddWithValue("$y0", item.Bounds[2]);
                    cmd.Parameters.AddWithValue("$y1", item.Bounds[3]);
                    cmd.Parameters.AddWithValue("$z0", item.Bounds[4]);
                    cmd.Parameters.AddWithValue("$z1", item.Bounds[5]);
                    cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    newId = Convert.ToInt32(cmd.ExecuteScalar());
                }

                // 将 PLY 副本写入 SavedClouds/{id}.ply
                string plyPath = Path.Combine(StoreDir, $"{newId}.ply");
                if (ExportPly(item.OriginalData!, plyPath))
                {
                    using var conn = Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE Clouds SET SourcePath=$p WHERE Id=$id";
                    cmd.Parameters.AddWithValue("$p", plyPath);
                    cmd.Parameters.AddWithValue("$id", newId);
                    cmd.ExecuteNonQuery();
                    item.SourcePath = plyPath;
                }
                item.Id = newId;
                return newId;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] 插入失败: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 更新已有记录的体积信息
        /// </summary>
        public static void UpdateVolume(PointCloudItem item)
        {
            if (item.Id <= 0) return;
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Clouds SET VolumeVoxel=$vv, VolumeConvex=$vc WHERE Id=$id";
                cmd.Parameters.AddWithValue("$vv", item.VoxelVolume);
                cmd.Parameters.AddWithValue("$vc", item.ConvexVolume);
                cmd.Parameters.AddWithValue("$id", item.Id);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] 更新体积失败: {ex.Message}");
            }
        }

        public static List<CloudRecord> GetAll()
        {
            var list = new List<CloudRecord>();
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Id, Name, SourcePath, PointCount, VolumeVoxel, VolumeConvex, BXmin, BXmax, BYmin, BYmax, BZmin, BZmax, CreatedAt FROM Clouds ORDER BY Id DESC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new CloudRecord
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        SourcePath = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        PointCount = reader.GetInt64(3),
                        VolumeVoxel = reader.GetDouble(4),
                        VolumeConvex = reader.GetDouble(5),
                        BXmin = reader.GetDouble(6), BXmax = reader.GetDouble(7),
                        BYmin = reader.GetDouble(8), BYmax = reader.GetDouble(9),
                        BZmin = reader.GetDouble(10), BZmax = reader.GetDouble(11),
                        CreatedAt = reader.IsDBNull(12) ? "" : reader.GetString(12)
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] 查询失败: {ex.Message}");
            }
            return list;
        }

        public static void Delete(int id)
        {
            try
            {
                string? path = null;
                using (var conn = Open())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT SourcePath FROM Clouds WHERE Id=$id";
                    cmd.Parameters.AddWithValue("$id", id);
                    var v = cmd.ExecuteScalar();
                    if (v != null && v != DBNull.Value) path = v.ToString();
                    using var del = conn.CreateCommand();
                    del.CommandText = "DELETE FROM Clouds WHERE Id=$id";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                }
                if (!string.IsNullOrEmpty(path) && File.Exists(path) && path.Contains("SavedClouds"))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB] 删除失败: {ex.Message}");
            }
        }

        public static CloudRecord? GetById(int id)
        {
            var all = GetAll();
            return all.Find(r => r.Id == id);
        }
    }
}
