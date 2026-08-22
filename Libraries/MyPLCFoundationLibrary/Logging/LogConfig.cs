using System;
using System.IO;

namespace MyPLCFoundationLibrary.Logging;

/// <summary>
/// 结构化日志配置。
/// </summary>
public sealed class LogConfig
{
    /// <summary>日志目录，默认 %LocalAppData%/PLCTesting/Logs。</summary>
    public string LogDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PLCTesting", "Logs");

    /// <summary>最小记录级别。</summary>
    public AppLogLevel MinLevel { get; set; } = AppLogLevel.Information;

    /// <summary>文件名前缀，例如 plctesting，最终文件为 plctesting-20260821.log。</summary>
    public string FileNamePrefix { get; set; } = "plctesting";

    /// <summary>是否同时输出到控制台（调试方便）。</summary>
    public bool ConsoleEnabled { get; set; } = true;

    /// <summary>保留的日志文件数量上限（按天滚动）。</summary>
    public int RetainedFileCountLimit { get; set; } = 31;

    /// <summary>写入每条日志的机器名（便于多机集中分析）。</summary>
    public string? MachineName { get; set; } = Environment.MachineName;

    /// <summary>应用名（作为日志 enrich 属性）。</summary>
    public string AppName { get; set; } = "PLCTesting";
}
