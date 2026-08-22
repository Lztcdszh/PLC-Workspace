namespace MyPLCFoundationLibrary.Logging;

/// <summary>
/// 应用日志级别（与 Serilog 级别对应）。
/// </summary>
public enum AppLogLevel
{
    Verbose = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5
}

/// <summary>
/// 结构化应用日志抽象。解耦具体日志框架，便于单元测试（可替换为内存实现）。
/// </summary>
public interface IAppLogger
{
    void Verbose(string message, params object[] args);
    void Debug(string message, params object[] args);
    void Information(string message, params object[] args);
    void Warning(string message, params object[] args);
    void Warning(Exception? ex, string message, params object[] args);
    void Error(Exception? ex, string message, params object[] args);
    void Fatal(Exception? ex, string message, params object[] args);
    /// <summary>返回一个带上下文字号的子日志器（用于区分模块/类）。</summary>
    IAppLogger ForContext(string context);
}
