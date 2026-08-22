using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MyPLCFoundationLibrary.Logging;

/// <summary>
/// 连接健康数据源：由应用层适配（读取当前 PLC 连接注册表），解耦具体通信库。
/// </summary>
public interface IConnectionHealthSource
{
    /// <summary>已注册连接总数。</summary>
    int Total { get; }
    /// <summary>其中已连接数量。</summary>
    int Connected { get; }
}

/// <summary>
/// 系统诊断服务：周期采集 CPU / 内存 / 磁盘 / 进程运行时长 / 通信健康，
/// 产出 <see cref="HealthSnapshot"/> 并通过 <see cref="HealthChanged"/> 广播，供诊断面板绑定。
/// CPU 基于进程处理器时间增量估算，跨平台可用（无需 PerformanceCounter）。
/// </summary>
public sealed class DiagnosticsService : IDisposable
{
    private readonly object _gate = new();
    private readonly List<IConnectionHealthSource> _sources = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastCpuStamp = DateTime.UtcNow;
    private TimeSpan _lastCpuTime;

    /// <summary>健康快照变更事件（每次采样触发）。</summary>
    public event EventHandler<HealthSnapshot>? HealthChanged;

    public DiagnosticsService(TimeSpan? sampleInterval = null)
    {
        _lastCpuTime = _process.TotalProcessorTime;
        var interval = sampleInterval ?? TimeSpan.FromSeconds(5);
        _ = SampleLoopAsync(interval, _cts.Token);
    }

    /// <summary>注册一个连接健康数据源（可多次注册，例如多个连接管理器）。</summary>
    public void RegisterHealthSource(IConnectionHealthSource source)
    {
        if (source == null) return;
        lock (_gate) _sources.Add(source);
    }

    public void UnregisterHealthSource(IConnectionHealthSource source)
    {
        if (source == null) return;
        lock (_gate) _sources.Remove(source);
    }

    /// <summary>立即采集一份健康快照。</summary>
    public HealthSnapshot GetSnapshot()
    {
        var now = DateTime.UtcNow;
        var cpu = ComputeCpu(now);
        double diskFree = 0;
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (drive.IsReady) diskFree = drive.TotalFreeSpace / 1048576.0;
        }
        catch { /* 某些环境无法读取磁盘信息，忽略 */ }

        int total = 0, connected = 0;
        lock (_gate)
        {
            foreach (var s in _sources)
            {
                try { total += s.Total; connected += s.Connected; }
                catch { /* 数据源异常不影响整体采样 */ }
            }
        }

        return new HealthSnapshot
        {
            Timestamp = DateTime.Now,
            CpuPercent = Math.Round(cpu, 1),
            MemoryWorkingSetMb = Math.Round(_process.WorkingSet64 / 1048576.0, 1),
            GcTotalMemoryMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            DiskFreeMb = Math.Round(diskFree, 1),
            Uptime = DateTime.Now - _process.StartTime,
            ConnectionsTotal = total,
            ConnectionsConnected = connected
        };
    }

    private double ComputeCpu(DateTime now)
    {
        var cpuTime = _process.TotalProcessorTime;
        var wallMs = (now - _lastCpuStamp).TotalMilliseconds;
        double cpu = 0;
        if (wallMs > 0)
            cpu = (cpuTime - _lastCpuTime).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100.0;
        _lastCpuStamp = now;
        _lastCpuTime = cpuTime;
        return Math.Max(0, Math.Min(100, cpu));
    }

    private async Task SampleLoopAsync(TimeSpan interval, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var snap = GetSnapshot();
                HealthChanged?.Invoke(this, snap);
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
        try { _process.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
