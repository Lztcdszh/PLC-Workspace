using MyPLCFoundationLibrary.Alarms;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PlcFoundationTests;

public class AlarmAuditTests
{
    private static string TempDb(string suffix) => Path.Combine(Path.GetTempPath(), $"aa_{suffix}_{Guid.NewGuid():N}.db");

    [Fact]
    public void Alarm_Lifecycle_StateMachine()
    {
        var db = TempDb("alarm");
        try
        {
            using var store = new AlarmStore(db);
            var rec = store.Raise("Pump1", AlarmSeverity.Critical, "过载");
            Assert.True(rec.Id > 0);
            Assert.Equal(AlarmState.Active, rec.State);
            Assert.Single(store.GetActive());
            Assert.Equal(1, store.CountUnacknowledged());

            // 同 Tag 重复 Raise 应去重（不新增）
            var rec2 = store.Raise("Pump1", AlarmSeverity.Critical, "过载更新");
            Assert.Equal(rec.Id, rec2.Id);
            Assert.Single(store.GetActive());

            Assert.True(store.Acknowledge(rec.Id, "admin"));
            Assert.Equal(AlarmState.Acknowledged, store.GetById(rec.Id)!.State);
            Assert.Equal(0, store.CountUnacknowledged());

            Assert.True(store.Clear(rec.Id));
            Assert.Equal(AlarmState.Cleared, store.GetById(rec.Id)!.State);
            Assert.Empty(store.GetActive());

            Assert.Single(store.GetHistory(tag: "Pump1"));
        }
        finally { try { if (File.Exists(db)) File.Delete(db); } catch { } }
    }

    [Fact]
    public void Audit_RecordAndQuery()
    {
        var db = TempDb("audit");
        try
        {
            using var store = new AuditStore(db);
            store.Record("admin", "Login", "", "登录成功", true);
            store.Record("admin", "WriteTag", "DB1.DBW10", "set=5", true);
            store.Record("guest", "Login", "", "密码错误", false);

            Assert.Equal(2, store.Query(action: "Login").Count);
            var fails = store.Query(action: "Login").Where(r => !r.Success).ToList();
            Assert.Single(fails);
            Assert.Equal("guest", fails[0].User);
        }
        finally { try { if (File.Exists(db)) File.Delete(db); } catch { } }
    }
}
