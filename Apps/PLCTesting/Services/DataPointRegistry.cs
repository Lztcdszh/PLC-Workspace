using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using PLCTesting.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PLCTesting.Services
{
    /// <summary>
    /// 数据点注册表：从 dataPoints.json 读取数据点定义，
    /// 启动时把每个点注册为 PLC 标签并开始监控；并为数据库记录与 MQTT 发布
    /// 提供"是否入库 / 是否发 MQTT"的查询，使数据处理完全可配置、可灵活扩展。
    /// </summary>
    public class DataPointRegistry : IDataPointSource
    {
        private static readonly string ConfigPath =
            Path.Combine(AppContext.BaseDirectory, "dataPoints.json");

        private List<DataPointConfig> _points = new();
        private Dictionary<string, DataPointConfig> _byName =
            new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DataPointConfig> _byAddress =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<DataPointConfig> Points => _points;

        public DataPointRegistry()
        {
            Reload();
        }

        /// <summary>
        /// 重新从磁盘加载数据点配置。
        /// 数据点编辑器「保存」后必须调用它 —— 否则注册表里仍是启动时那一份，
        /// 用户在界面上改的 PublishMqtt / LogToDb / 新增删除点都不会生效，直到重启软件。
        /// <para>
        /// 采用「写时复制」：在临时集合里装载完成后一次性替换引用。
        /// 不能原地 Clear()+Add —— 后台定时发布线程可能正枚举旧集合，
        /// 那会抛 "Collection was modified" 并让定时发布循环永久退出（静默失聪）。
        /// </para>
        /// </summary>
        public void Reload()
        {
            var points = new List<DataPointConfig>();
            var byName = new Dictionary<string, DataPointConfig>(StringComparer.OrdinalIgnoreCase);
            var byAddress = new Dictionary<string, DataPointConfig>(StringComparer.OrdinalIgnoreCase);

            var ok = false;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var list = JsonSerializer.Deserialize<List<DataPointConfig>>(json);
                    if (list != null)
                    {
                        foreach (var p in list.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                            Add(points, byName, byAddress, p);
                        ok = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataPointRegistry] 加载失败: {ex.Message}");
            }

            // 无配置或读取失败时，回退到默认两个点，保持原有行为
            if (!ok)
                SeedDefaults(points, byName, byAddress);

            // 原子替换（引用赋值，读者要么看到旧快照、要么看到新快照）
            _points = points;
            _byName = byName;
            _byAddress = byAddress;
        }

        private static void SeedDefaults(
            List<DataPointConfig> points,
            Dictionary<string, DataPointConfig> byName,
            Dictionary<string, DataPointConfig> byAddress)
        {
            Add(points, byName, byAddress, new DataPointConfig
            {
                Name = "DB1.DBW230",
                Address = "DB1.DBW230",
                DataType = "Int16",
                Description = "小车状态"
            });
            Add(points, byName, byAddress, new DataPointConfig
            {
                Name = "DB1.DBB172",
                Address = "DB1.DBB172",
                DataType = "UInt8",
                Description = "小车故障代码"
            });
        }

        private static void Add(
            List<DataPointConfig> points,
            Dictionary<string, DataPointConfig> byName,
            Dictionary<string, DataPointConfig> byAddress,
            DataPointConfig p)
        {
            points.Add(p);
            byName[p.Name] = p;
            if (!string.IsNullOrWhiteSpace(p.Address))
                byAddress[p.Address] = p;
        }

        /// <summary>把配置中的每个数据点注册为 PLC 标签并开始监控轮询。</summary>
        public void ApplyTo(IConnectionManager connection)
        {
            if (connection == null) return;
            var snapshot = _points;   // 取一次快照，避免循环中被 Reload 换掉
            foreach (var p in snapshot)
            {
                var tag = CreateTag(p);
                if (tag == null) continue;
                tag.IsMonitored = true;

                // 同名标签已存在（例如配置改了之后重新 ApplyTo）→ 就地更新，不要重复 AddTag：
                // TagService.AddTag 用 Tags.Contains 去重，而 PLCTag 没重写 Equals，
                // 那是引用比较，重复注册会让同一点在集合里堆叠多份、重复轮询。
                var existing = connection.GetTag(tag.Name);
                if (existing != null)
                {
                    existing.Address = tag.Address;
                    existing.DataType = tag.DataType;
                    existing.Description = tag.Description;
                    existing.IsMonitored = true;
                    continue;
                }

                connection.AddTag(tag);
            }
        }

        private static PLCTag? CreateTag(DataPointConfig p)
        {
            var name = string.IsNullOrWhiteSpace(p.Name) ? p.Address : p.Name;
            var addr = string.IsNullOrWhiteSpace(p.Address) ? p.Name : p.Address;
            return p.DataType?.Trim().ToLowerInvariant() switch
            {
                "bool" => PLCTag.CreateBool(name, addr, p.Description),
                "int" => PLCTag.CreateInt(name, addr, p.Description),
                "int16" or "short" => PLCTag.CreateShort(name, addr, p.Description),
                "word" or "uint16" or "ushort" => PLCTag.CreateWord(name, addr, p.Description),
                "uint8" or "byte" => PLCTag.CreateByte(name, addr, p.Description),
                "dword" or "uint" or "uint32" => PLCTag.CreateDWord(name, addr, p.Description),
                "double" => PLCTag.CreateDouble(name, addr, p.Description),
                "string" => PLCTag.CreateString(name, addr, 32, p.Description),
                _ => PLCTag.CreateFloat(name, addr, p.Description)
            };
        }

        public bool ShouldLog(string name) => TryGet(name, out var p) && p.LogToDb;
        public bool ShouldPublish(string name) => TryGet(name, out var p) && p.PublishMqtt;

        /// <summary>按名称或地址查找数据点配置（不区分大小写）。</summary>
        public bool TryGet(string name, out DataPointConfig? config)
        {
            if (_byName.TryGetValue(name, out var byName))
            {
                config = byName;
                return true;
            }
            if (_byAddress.TryGetValue(name, out var byAddr))
            {
                config = byAddr;
                return true;
            }
            config = null;
            return false;
        }
    }
}
