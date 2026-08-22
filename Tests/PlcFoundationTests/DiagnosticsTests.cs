using MyPLCFoundationLibrary.Logging;
using System;
using Xunit;

namespace PlcFoundationTests;

public class DiagnosticsTests
{
    private sealed class FakeSource : IConnectionHealthSource
    {
        public int Total { get; set; }
        public int Connected { get; set; }
    }

    [Fact]
    public void Diagnostics_Snapshot_ReflectsConnections()
    {
        using var svc = new DiagnosticsService(TimeSpan.FromHours(1)); // 大间隔，避免自动循环干扰
        var snap0 = svc.GetSnapshot();
        Assert.InRange(snap0.CpuPercent, 0, 100);
        Assert.True(snap0.MemoryWorkingSetMb > 0);

        var src = new FakeSource { Total = 3, Connected = 2 };
        svc.RegisterHealthSource(src);
        var snap1 = svc.GetSnapshot();
        Assert.Equal(3, snap1.ConnectionsTotal);
        Assert.Equal(2, snap1.ConnectionsConnected);

        svc.UnregisterHealthSource(src);
        var snap2 = svc.GetSnapshot();
        Assert.Equal(0, snap2.ConnectionsTotal);
    }
}
