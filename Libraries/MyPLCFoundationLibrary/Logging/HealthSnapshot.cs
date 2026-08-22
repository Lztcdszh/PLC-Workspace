using System;

namespace MyPLCFoundationLibrary.Logging;

/// <summary>
/// 系统健康快照。由 <see cref="DiagnosticsService"/> 周期采集。
/// </summary>
public sealed class HealthSnapshot
{
    /// <summary>采集时间（本地）。</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>CPU 占用率（0-100），基于进程处理器时间增量估算，跨平台可用。</summary>
    public double CpuPercent { get; set; }

    /// <summary>进程工作集内存（MB）。</summary>
    public double MemoryWorkingSetMb { get; set; }

    /// <summary>GC 总内存（MB）。</summary>
    public double GcTotalMemoryMb { get; set; }

    /// <summary>程序所在磁盘剩余空间（MB）。</summary>
    public double DiskFreeMb { get; set; }

    /// <summary>进程已运行时间。</summary>
    public TimeSpan Uptime { get; set; }

    /// <summary>已注册的 PLC 连接总数。</summary>
    public int ConnectionsTotal { get; set; }

    /// <summary>其中已连接的数量。</summary>
    public int ConnectionsConnected { get; set; }

    public override string ToString() =>
        $"CPU {CpuPercent:F1}% | MEM {MemoryWorkingSetMb:F0}MB | DISK {DiskFreeMb:F0}MB | 连接 {ConnectionsConnected}/{ConnectionsTotal} | 运行 {Uptime:hh\\:mm\\:ss}";
}
