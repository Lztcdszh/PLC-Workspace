#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MyPLCManagerLibrary.Interfaces;
using MyPLCFoundationLibrary.Alarms;
using MyPLCFoundationLibrary.Historian;
using MyPLCFoundationLibrary.Logging;
using PLCTesting.Models;

namespace PLCTesting.Services
{
    /// <summary>
    /// P0 基础库接入层：集中持有结构化日志、过程历史库、报警/审计库、系统诊断，
    /// 并提供把现有 DataIngestionService 的 IChangeLogSink 桥接到历史库的适配器。
    /// 在 App 启动处（如 App.Startup 或 MainWindowViewModel 构造）调用 <see cref="Initialize"/>。
    /// </summary>
    public static class FoundationServices
    {
        public static HistorianStore? Historian { get; private set; }
        public static AlarmStore? Alarms { get; private set; }
        public static AuditStore? Audit { get; private set; }
        public static DiagnosticsService? Diagnostics { get; private set; }
        public static IAppLogger Logger => StructuredLogger.Current;

        public static void Initialize(
            HistorianConfig? historianConfig = null,
            string? historianDb = null,
            string? alarmsDb = null,
            string? auditDb = null,
            bool startDiagnostics = true)
        {
            StructuredLogger.Init(new LogConfig
            {
                LogDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PLCTesting", "Logs"),
                ConsoleEnabled = true,
                MinLevel = AppLogLevel.Information,
                FileNamePrefix = "plctesting"
            });

            var histCfg = historianConfig ?? new HistorianConfig
            {
                SamplingIntervalMs = 1000,
                RetentionDays = 30,
                AggregationEnabled = true,
                AggregationIntervalMs = 60_000
            };
            Historian = new HistorianStore(historianDb ?? HistorianStore.DefaultDbPath, histCfg);
            Alarms = new AlarmStore(alarmsDb ?? AlarmStore.DefaultDbPath);
            Audit = new AuditStore(auditDb ?? AuditStore.DefaultDbPath);

            if (startDiagnostics)
                Diagnostics = new DiagnosticsService(TimeSpan.FromSeconds(5));

            Logger.Information("P0 基础库已初始化（日志 / 历史 / 报警 / 审计 / 诊断）");
        }

        /// <summary>启动历史库自动采样。sampler 由应用提供，返回当前需要采样的标签值。</summary>
        public static void StartHistorian(Func<CancellationToken, Task<IReadOnlyList<SampleInput>>> sampler)
            => Historian?.Start(sampler);

        public static void Shutdown()
        {
            Historian?.Stop();
            Diagnostics?.Dispose();
            Audit?.Dispose();
            Alarms?.Dispose();
            StructuredLogger.CloseAndFlush();
        }
    }

    /// <summary>
    /// 把 DataIngestionService 的变更日志事件桥接为历史库原始采样点。
    /// 现有"标签变化即落盘"的数据同时进入过程历史库，可回放趋势。
    /// </summary>
    public sealed class ChangeLogToHistorianSink : IChangeLogSink
    {
        private readonly HistorianStore? _historian;
        public ChangeLogToHistorianSink(HistorianStore? historian = null) => _historian = historian ?? FoundationServices.Historian;

        public void Record(ChangeLogRecord record)
        {
            if (record == null || _historian == null) return;
            if (double.TryParse(record.NewValue, out var v))
                _historian.InsertRaw(record.TagName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), v, true);
        }
    }

    /// <summary>组合多个 IChangeLogSink，使一份变更日志同时写入多处（原有库 + 历史库 + …）。</summary>
    public sealed class CompositeChangeLogSink : IChangeLogSink
    {
        private readonly IChangeLogSink[] _sinks;
        public CompositeChangeLogSink(params IChangeLogSink[] sinks) => _sinks = sinks ?? Array.Empty<IChangeLogSink>();

        public void Record(ChangeLogRecord record)
        {
            foreach (var s in _sinks)
            {
                try { s.Record(record); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CompositeSink] {ex.Message}"); }
            }
        }
    }

    /// <summary>把 PLC 连接注册表适配为诊断服务的连接健康数据源。</summary>
    public sealed class ConnectionHealthAdapter : IConnectionHealthSource
    {
        private readonly Func<IEnumerable<IConnectionManager>> _source;
        public ConnectionHealthAdapter(Func<IEnumerable<IConnectionManager>> source) => _source = source;

        public int Total => _source().Count();
        public int Connected => _source().Count(c => c.IsConnected);
    }
}
