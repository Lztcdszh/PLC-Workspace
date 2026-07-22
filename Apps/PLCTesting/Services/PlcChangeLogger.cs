using MyPLCManagerLibrary;
using MyPLCManagerLibrary.Interfaces;
using MyPLCManagerLibrary.Model;
using PLCTesting.Models;
using PLCTesting.Services;
using System;
using System.Windows;

namespace PLCTesting.Services
{
    /// <summary>
    /// PLC 变化日志记录器：订阅 IConnectionManager.TagValueChanged 事件，
    /// 仅记录 dataPoints.json 中标记为 LogToDb 的数据点（灵活、可配置）。
    /// 所有 PLC 通信依赖都集中在 app 层，不污染控件库。
    /// </summary>
    public class PlcChangeLogger : IDisposable
    {
        private readonly IConnectionManager _connection;
        private readonly IChangeLogSink _sink;
        private readonly IDataPointSource _registry;
        private bool _disposed;

        /// <summary>
        /// 构造变化日志记录器。
        /// </summary>
        /// <param name="connection">PLC 连接（订阅标签变化、读取标签元数据）</param>
        /// <param name="sink">日志落盘目标（如 SQLite 仓储），解耦具体存储</param>
        /// <param name="registry">数据点来源，决定哪些点需要入库</param>
        public PlcChangeLogger(IConnectionManager connection, IChangeLogSink sink, IDataPointSource registry)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));

            _connection.TagValueChanged += OnTagValueChanged;
        }

        private void OnTagValueChanged(object sender, TagService.TagChangedEventArgs e)
        {
            if (e == null) return;
            // 只记录配置里标记为 LogToDb 的数据点 —— 加/减数据只改 dataPoints.json
            if (!_registry.ShouldLog(e.TagName)) return;

            try
            {
                var tag = _connection.GetTag(e.TagName);
                _registry.TryGet(e.TagName, out var cfg);
                var record = new Models.ChangeLogRecord
                {
                    TimeStamp = DateTime.Now,
                    TagName = tag?.Name ?? e.TagName,
                    TagAddress = cfg?.Address ?? tag?.Address ?? e.TagName,
                    DataType = cfg?.DataType ?? tag?.DataType?.Name ?? string.Empty,
                    Description = cfg?.Description ?? tag?.Description ?? string.Empty,
                    OldValue = e.OldValue?.ToString() ?? string.Empty,
                    NewValue = e.NewValue?.ToString() ?? string.Empty
                };
                _sink.Record(record);
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
