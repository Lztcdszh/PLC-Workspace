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

        private readonly List<DataPointConfig> _points = new();
        private readonly Dictionary<string, DataPointConfig> _byName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DataPointConfig> _byAddress =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<DataPointConfig> Points => _points;

        public DataPointRegistry()
        {
            Load();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var list = JsonSerializer.Deserialize<List<DataPointConfig>>(json);
                    if (list != null)
                    {
                        foreach (var p in list.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                            Add(p);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataPointRegistry] 加载失败: {ex.Message}");
            }

            // 无配置或读取失败时，回退到默认两个点，保持原有行为
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            Add(new DataPointConfig
            {
                Name = "DB1.DBW230",
                Address = "DB1.DBW230",
                DataType = "Int16",
                Description = "小车状态"
            });
            Add(new DataPointConfig
            {
                Name = "DB1.DBB172",
                Address = "DB1.DBB172",
                DataType = "UInt8",
                Description = "小车故障代码"
            });
        }

        private void Add(DataPointConfig p)
        {
            _points.Add(p);
            _byName[p.Name] = p;
            if (!string.IsNullOrWhiteSpace(p.Address))
                _byAddress[p.Address] = p;
        }

        /// <summary>把配置中的每个数据点注册为 PLC 标签并开始监控轮询。</summary>
        public void ApplyTo(IConnectionManager connection)
        {
            if (connection == null) return;
            foreach (var p in _points)
            {
                var tag = CreateTag(p);
                if (tag != null)
                {
                    tag.IsMonitored = true;
                    connection.AddTag(tag);
                }
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
