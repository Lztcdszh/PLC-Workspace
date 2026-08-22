using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace MyPLCFoundationLibrary.Logging;

/// <summary>
/// 基于 Serilog 的结构化日志实现：文件按天滚动 + 可选控制台，带 Machine/App/Context enrich。
/// 通过 <see cref="Init"/> 初始化；未初始化时 <see cref="Current"/> 会以默认配置惰性初始化。
/// </summary>
public sealed class StructuredLogger : IAppLogger
{
    private static readonly object _gate = new();
    private static ILogger? _serilog;
    private static LogConfig? _config;
    private static readonly Lazy<IAppLogger> _current = new(() => EnsureInitialized().CreateScoped(null));

    private readonly string? _context;
    private readonly ILogger? _baseLogger;

    private StructuredLogger(string? context, ILogger? baseLogger)
    {
        _context = context;
        _baseLogger = baseLogger;
    }

    /// <summary>当前日志器（惰性初始化为默认配置）。</summary>
    public static IAppLogger Current => _current.Value;

    /// <summary>
    /// 使用指定配置初始化全局日志器。应在程序启动最早处调用一次。
    /// </summary>
    public static void Init(LogConfig config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        lock (_gate)
        {
            _config = config;
            var level = MapLevel(config.MinLevel);
            var dir = config.LogDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var builder = new LoggerConfiguration()
                .MinimumLevel.Is(level)
                .Enrich.WithProperty("Machine", config.MachineName ?? Environment.MachineName)
                .Enrich.WithProperty("App", config.AppName);

            if (config.ConsoleEnabled)
                builder = builder.WriteTo.Console(level);

            builder = builder.WriteTo.File(
                Path.Combine(dir, $"{config.FileNamePrefix}-.log"),
                restrictedToMinimumLevel: level,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: config.RetainedFileCountLimit,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Machine} {App} {Context} {Message:lj}{NewLine}{Exception}");

            _serilog = builder.CreateLogger();
        }
    }

    /// <summary>关闭并刷新全局日志器（确保缓冲写入磁盘）。程序退出前调用。</summary>
    public static void CloseAndFlush()
    {
        lock (_gate)
        {
            try { (_serilog as IDisposable)?.Dispose(); } catch { }
            _serilog = null;
        }
    }

    private static StructuredLogger EnsureInitialized()
    {
        lock (_gate)
        {
            if (_serilog == null) Init(new LogConfig());
            return new StructuredLogger(null, _serilog);
        }
    }

    private StructuredLogger CreateScoped(string? context) => new(context, _serilog);

    public IAppLogger ForContext(string context) => CreateScoped(context);

    public void Verbose(string message, params object[] args) => Write(LogEventLevel.Verbose, null, message, args);
    public void Debug(string message, params object[] args) => Write(LogEventLevel.Debug, null, message, args);
    public void Information(string message, params object[] args) => Write(LogEventLevel.Information, null, message, args);
    public void Warning(string message, params object[] args) => Write(LogEventLevel.Warning, null, message, args);
    public void Warning(Exception? ex, string message, params object[] args) => Write(LogEventLevel.Warning, ex, message, args);
    public void Error(Exception? ex, string message, params object[] args) => Write(LogEventLevel.Error, ex, message, args);
    public void Fatal(Exception? ex, string message, params object[] args) => Write(LogEventLevel.Fatal, ex, message, args);

    private void Write(LogEventLevel level, Exception? ex, string message, object[] args)
    {
        var logger = _baseLogger ?? _serilog;
        if (logger == null) return;
        var scoped = _context == null ? logger : logger.ForContext("Context", _context);
        if (args == null || args.Length == 0)
            scoped.Write(level, ex, message);
        else
            scoped.Write(level, ex, message, args);
    }

    private static LogEventLevel MapLevel(AppLogLevel level) => level switch
    {
        AppLogLevel.Verbose => LogEventLevel.Verbose,
        AppLogLevel.Debug => LogEventLevel.Debug,
        AppLogLevel.Information => LogEventLevel.Information,
        AppLogLevel.Warning => LogEventLevel.Warning,
        AppLogLevel.Error => LogEventLevel.Error,
        AppLogLevel.Fatal => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };
}
