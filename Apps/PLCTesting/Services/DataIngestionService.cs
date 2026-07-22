using PLCTesting.Models;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace PLCTesting.Services
{
    /// <summary>
    /// 数据接入服务：统一入口，把"任意数据"（PLC 标签变化之外的数据，例如计算结果、
    /// 外部系统接口数据、定时采集值等）分发到各个数据目标。
    ///
    /// 关键设计：**本服务不依赖任何具体存储或消息中间件**。它只认识两个抽象：
    ///   - IChangeLogSink  （日志落盘目标，当前实现为 SQLite；可换成文件/其他库而无需改本类）
    ///   - IMessagePublisher（消息发布目标，当前实现为 MQTT；可换成 HTTP/串口而无需改本类）
    /// 两个 sink 彼此完全解耦：缺数据库只影响落盘、缺 MQTT 只影响发布，互不影响。
    ///
    /// 与 dataPoints.json 联动：若配置中存在同名数据点，则沿用其 LogToDb / PublishMqtt
    /// 开关与 Description / DataType；否则用调用方显式传入的开关（默认都开启）。
    ///
    /// 用法示例（任意代码处）：
    /// <code>
    /// // 1) 通过主窗口 ViewModel 暴露的属性（推荐，跟随同一实例）
    /// if (Application.Current.MainWindow?.DataContext is MainWindowViewModel vm)
    ///     await vm.DataIngestion.RecordAsync("视觉算法温度", 36.5, "视觉估算温度", "Double");
    ///
    /// // 2) 或任意代码处通过静态当前实例（无需持有 VM 引用）
    /// await DataIngestionService.Current.RecordAsync("环境湿度", 58.2, "车间湿度", "Double");
    /// </code>
    /// </summary>
    public class DataIngestionService
    {
        private readonly IChangeLogSink? _logSink;
        private readonly IMessagePublisher? _publisher;
        private readonly IDataPointSource _registry;

        /// <summary>当前运行实例（由 MainWindowViewModel 构造时赋值），便于任意代码直接调用。</summary>
        public static DataIngestionService? Current { get; set; }

        /// <summary>
        /// 构造数据接入服务。
        /// </summary>
        /// <param name="registry">数据点来源（决定同名点的入库/发布开关），解耦具体实现。</param>
        /// <param name="logSink">日志落盘目标；传 null 表示本次运行不需要写库（MQTT 仍可用）。</param>
        /// <param name="publisher">消息发布目标；传 null 表示本次运行不需要发布（写库仍可用）。</param>
        public DataIngestionService(IDataPointSource registry, IChangeLogSink? logSink = null, IMessagePublisher? publisher = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _logSink = logSink;
            _publisher = publisher;
        }

        /// <summary>
        /// 记录一条任意数据：按开关分别写库、发 MQTT（两者独立，互不影响）。
        /// </summary>
        /// <param name="name">数据点名称（MQTT 主题末段、日志 TagName）</param>
        /// <param name="value">任意值（数字 / 字符串 / bool 等）</param>
        /// <param name="description">业务描述，写入日志 Description 列并随 MQTT 负载发出</param>
        /// <param name="dataType">类型字符串，默认 "String"</param>
        /// <param name="logToDb">是否入库；为空时若配置有同名点则用其 LogToDb，否则默认 true</param>
        /// <param name="publishMqtt">是否发 MQTT；为空时若配置有同名点则用其 PublishMqtt，否则默认 true</param>
        public async Task RecordAsync(string name, object? value, string? description = null,
            string? dataType = null, bool? logToDb = null, bool? publishMqtt = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            string desc = description ?? string.Empty;
            string type = string.IsNullOrWhiteSpace(dataType) ? "String" : dataType!;
            bool doLog;
            bool doPub;

            if (_registry.TryGet(name, out var cfg) && cfg != null)
            {
                // 同名配置点存在 -> 以配置文件为准，仅在调用方未显式覆盖时使用
                doLog = logToDb ?? cfg.LogToDb;
                doPub = publishMqtt ?? cfg.PublishMqtt;
                if (string.IsNullOrWhiteSpace(desc)) desc = cfg.Description;
                if (type == "String") type = cfg.DataType;
            }
            else
            {
                doLog = logToDb ?? true;
                doPub = publishMqtt ?? true;
            }

            if (doLog && _logSink != null)
            {
                try
                {
                    _logSink.Record(new ChangeLogRecord
                    {
                        TimeStamp = DateTime.Now,
                        TagName = name,
                        TagAddress = name,
                        DataType = type,
                        Description = desc,
                        OldValue = string.Empty,
                        NewValue = value?.ToString() ?? string.Empty
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DataIngestion] 写入日志失败: {ex.Message}");
                }
            }

            if (doPub && _publisher != null)
            {
                await _publisher.PublishAsync(name, name, value!, desc, type);
            }
        }
    }
}
