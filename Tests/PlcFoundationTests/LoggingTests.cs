using MyPLCFoundationLibrary.Logging;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PlcFoundationTests;

public class LoggingTests
{
    [Fact]
    public void StructuredLogger_WritesToFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"log_{Guid.NewGuid():N}");
        try
        {
            StructuredLogger.Init(new LogConfig
            {
                LogDirectory = dir,
                ConsoleEnabled = false,
                MinLevel = AppLogLevel.Information,
                FileNamePrefix = "testlog"
            });
            var log = StructuredLogger.Current;
            log.Information("结构化日志测试 INFO {Value}", 42);
            log.Warning("结构化日志测试 WARN");
            log.Error(new Exception("boom"), "结构化日志测试 ERR");

            StructuredLogger.CloseAndFlush();

            var files = Directory.GetFiles(dir, "*.log");
            Assert.NotEmpty(files);
            var text = string.Join("\n", files.Select(File.ReadAllText));
            Assert.Contains("结构化日志测试 INFO", text);
            Assert.Contains("结构化日志测试 WARN", text);
            Assert.Contains("结构化日志测试 ERR", text);
            Assert.Contains("boom", text);
        }
        finally
        {
            try { StructuredLogger.CloseAndFlush(); } catch { }
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch { }
        }
    }
}
