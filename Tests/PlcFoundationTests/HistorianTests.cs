using MyPLCFoundationLibrary.Historian;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PlcFoundationTests;

public class HistorianTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"hist_{Guid.NewGuid():N}.db");

    [Fact]
    public void Aggregate_MinAvgMaxLast_Count_ExcludesBadQuality()
    {
        var db = TempDb();
        try
        {
            var cfg = new HistorianConfig { AggregationIntervalMs = 60_000, RetentionDays = 30 };
            using var store = new HistorianStore(db, cfg);
            var baseTs = 1_700_000_000_000L;
            store.InsertRaw("T1", baseTs, 10, good: true);
            store.InsertRaw("T1", baseTs + 1_000, 20, good: true);
            store.InsertRaw("T1", baseTs + 2_000, 30, good: true);
            store.InsertRaw("T1", baseTs + 3_000, 999, good: false); // 坏点不参与聚合
            store.AggregateAsync().Wait();

            var agg = store.GetAggregated("T1", baseTs - 1, baseTs + 10_000, 60_000);
            Assert.Single(agg);
            var b = agg[0];
            Assert.Equal(10, b.Min);
            Assert.Equal(30, b.Max);
            Assert.Equal(20, b.Avg, 3);
            Assert.Equal(3, b.Count);
            Assert.Equal(30, b.Last); // 最新好点
        }
        finally { try { if (File.Exists(db)) File.Delete(db); } catch { } }
    }

    [Fact]
    public void Retention_DeletesOldRaw_KeepsRecent()
    {
        var db = TempDb();
        try
        {
            var cfg = new HistorianConfig { AggregationIntervalMs = 60_000, RetentionDays = 30 };
            using var store = new HistorianStore(db, cfg);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var old = now - (long)40 * 86_400_000L;
            store.InsertRaw("T2", old, 5, good: true);
            store.InsertRaw("T2", now, 7, good: true);
            store.AggregateAsync().Wait();
            store.ApplyRetentionAsync().Wait();

            var raw = store.GetRaw("T2", old - 1_000, now + 1_000);
            Assert.All(raw, p => Assert.True(p.Ts > old));
            Assert.Contains(raw, p => p.Ts == now);
        }
        finally { try { if (File.Exists(db)) File.Delete(db); } catch { } }
    }
}
