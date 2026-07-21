using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using PLCTesting.Services;
using System;
using System.Collections.Generic;
using System.Windows;

namespace PLCTesting.Services
{
    /// <summary>
    /// PLC 标签变化日志记录器：订阅 IConnectionManager.TagValueChanged 事件，
    /// 把 DB1.DBW230 / DB1.DBB172 的变化写入 SQLite。
    /// 所有 PLC 通信依赖都集中在 app 层，不污染控件库。
    /// </summary>
    public class PlcChangeLogger : IDisposable
    {
        private readonly IConnectionManager _connection;
        private readonly ChangeLogRepository _repository;
        private readonly HashSet<string> _monitoredNames;
        private readonly Dictionary<string, string> _addressByName;
        private readonly Dictionary<string, string> _dataTypeByName;
        private bool _disposed;

        public PlcChangeLogger(IConnectionManager connection, ChangeLogRepository repository)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));

            // 这里用 tag 名称 = PLC 地址，避免与 UI 控件中的命名耦合；
            // 同时自己主动注册这两个标签，保证无论 UI 控件是否自动注册都能监听。
            var tag230 = PLCTag.CreateShort("DB1.DBW230", "DB1.DBW230", "Int16");
            var tag172 = PLCTag.CreateByte("DB1.DBB172", "DB1.DBB172", "UInt8");
            tag230.IsMonitored = true;
            tag172.IsMonitored = true;
            _connection.AddTag(tag230);
            _connection.AddTag(tag172);

            _monitoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "DB1.DBW230", "DB1.DBB172"
            };
            _addressByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DB1.DBW230"] = "DB1.DBW230",
                ["DB1.DBB172"] = "DB1.DBB172"
            };
            _dataTypeByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DB1.DBW230"] = "Int16",
                ["DB1.DBB172"] = "UInt8"
            };

            _connection.TagValueChanged += OnTagValueChanged;
        }

        private void OnTagValueChanged(object sender, TagService.TagChangedEventArgs e)
        {
            if (e == null || !_monitoredNames.Contains(e.TagName)) return;

            try
            {
                var record = new Models.ChangeLogRecord
                {
                    TimeStamp = DateTime.Now,
                    TagName = e.TagName,
                    TagAddress = _addressByName[e.TagName],
                    DataType = _dataTypeByName[e.TagName],
                    OldValue = e.OldValue?.ToString() ?? string.Empty,
                    NewValue = e.NewValue?.ToString() ?? string.Empty
                };
                _repository.Insert(record);
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    System.Diagnostics.Debug.WriteLine($"[PlcChangeLogger] 写入日志失败: {ex.Message}");
                });
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connection.TagValueChanged -= OnTagValueChanged;
                _disposed = true;
            }
        }
    }
}
