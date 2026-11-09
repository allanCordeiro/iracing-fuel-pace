using System.Text;
using System.Text.Json;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;

namespace FuelPace.Tests.Diagnostics;

public class DiagLogTests
{
    private static string ToJson(in DiagRecord r)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) DiagJsonFormatter.Write(w, in r);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public void Emit_below_level_is_ignored()
    {
        var log = new DiagLog(DiagLevel.Info);
        log.Emit(DiagLevel.Debug, DiagEvent.FrameGap, 1.0);
        Assert.False(log.IsEnabled(DiagLevel.Debug));
        Assert.Equal(0, log.DrainRecords(new DiagRecord[8]));
        log.Level = DiagLevel.Debug;
        log.Emit(DiagLevel.Debug, DiagEvent.FrameGap, 1.0);
        Assert.Equal(1, log.DrainRecords(new DiagRecord[8]));
    }

    [Fact]
    public void Null_log_records_nothing()
    {
        DiagLog.Null.Emit(DiagLevel.Error, DiagEvent.UserMark, 0);
        Assert.Equal(0, DiagLog.Null.DrainRecords(new DiagRecord[2]));
    }

    [Fact]
    public void Emit_and_drain_preserve_order_and_fields()
    {
        var log = new DiagLog(DiagLevel.Trace);
        for (int i = 0; i < 3; i++)
            log.Emit(DiagLevel.Info, DiagEvent.LapCompleted, 10.5 + i, i0: i, i1: 7, a: 1.5, b: 2.5, c: 3.5, d: 4.5);
        var buf = new DiagRecord[8];
        Assert.Equal(3, log.DrainRecords(buf));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i, buf[i].I0);
            Assert.Equal(7, buf[i].I1);
            Assert.Equal(10.5 + i, buf[i].SessionTime);
            Assert.Equal(DiagEvent.LapCompleted, buf[i].Event);
            Assert.Equal(DiagLevel.Info, buf[i].Level);
            Assert.Equal(4.5, buf[i].D);
            Assert.True(buf[i].WallTicksUtc > 0);
        }
        Assert.True(buf[0].Seq < buf[1].Seq && buf[1].Seq < buf[2].Seq);
        Assert.Equal(0, log.DrainRecords(buf));
    }

    [Fact]
    public void Drain_respects_destination_size()
    {
        var log = new DiagLog(DiagLevel.Trace);
        for (int i = 0; i < 5; i++) log.Emit(DiagLevel.Info, DiagEvent.UserMark, 0, i0: i);
        var buf = new DiagRecord[2];
        Assert.Equal(2, log.DrainRecords(buf));
        Assert.Equal(2, log.DrainRecords(buf));
        Assert.Equal(1, log.DrainRecords(buf));
        Assert.Equal(4, buf[0].I0);
    }

    [Fact]
    public void Full_ring_drops_and_counts()
    {
        var log = new DiagLog(DiagLevel.Trace, capacity: 4);
        for (int i = 0; i < 6; i++) log.Emit(DiagLevel.Info, DiagEvent.UserMark, 0, i0: i);
        var buf = new DiagRecord[16];
        Assert.Equal(4, log.DrainRecords(buf));
        Assert.Equal(2, log.Dropped);
        Assert.Equal(3, buf[3].I0);
        // ring is reusable after draining (wraps)
        for (int i = 0; i < 4; i++) log.Emit(DiagLevel.Info, DiagEvent.UserMark, 0, i0: 10 + i);
        Assert.Equal(4, log.DrainRecords(buf));
        Assert.Equal(10, buf[0].I0);
        Assert.Equal(13, buf[3].I0);
    }

    [Fact]
    public void Emit_allocates_nothing_at_trace()
    {
        var log = new DiagLog(DiagLevel.Trace, capacity: 2048);
        var buf = new DiagRecord[2048];
        // warm up
        for (int i = 0; i < 1000; i++) log.Emit(DiagLevel.Trace, DiagEvent.StrategyTrace, 1.0, 1, 2, 1, 2, 3, 4);
        log.DrainRecords(buf);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 100; n++)
        {
            for (int i = 0; i < 1000; i++) log.Emit(DiagLevel.Trace, DiagEvent.StrategyTrace, 1.0, i, 2, 1, 2, 3, 4);
            log.DrainRecords(buf);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Concurrent_emitters_do_not_lose_or_corrupt()
    {
        var log = new DiagLog(DiagLevel.Trace, capacity: 65536);
        var threads = new Thread[4];
        for (int t = 0; t < 4; t++)
        {
            int id = t;
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < 10_000; i++)
                    log.Emit(DiagLevel.Info, DiagEvent.UserMark, id, i0: id, i1: i, a: id * 100000.0 + i);
            });
            threads[t].Start();
        }
        foreach (var th in threads) th.Join();
        var buf = new DiagRecord[65536];
        Assert.Equal(40_000, log.DrainRecords(buf));
        Assert.Equal(0, log.Dropped);
        var seqs = new HashSet<long>();
        for (int i = 0; i < 40_000; i++)
        {
            Assert.True(seqs.Add(buf[i].Seq));
            Assert.Equal(buf[i].I0 * 100000.0 + buf[i].I1, buf[i].A);
            Assert.Equal((double)buf[i].I0, buf[i].SessionTime);
        }
    }

    [Fact]
    public void Json_line_has_named_fields_and_reason_codes()
    {
        var r = new DiagRecord
        {
            Seq = 5, WallTicksUtc = new DateTime(2026, 11, 9, 12, 30, 45, 123, DateTimeKind.Utc).Ticks,
            SessionTime = 4300.5, Event = DiagEvent.LapCompleted, Level = DiagLevel.Info,
            I0 = 19, I1 = (int)(LapReasons.Incident | LapReasons.OffTrack),
            A = 93.175, B = 2.388, C = 55.3, D = 93.18,
        };
        Assert.Equal(
            "{\"t\":\"2026-11-09T12:30:45.123Z\",\"seq\":5,\"st\":4300.5,\"lvl\":\"info\",\"cat\":\"laps\",\"ev\":\"LapCompleted\"," +
            "\"lap\":19,\"reasons\":[\"incident\",\"offtrack\"],\"timeS\":93.175,\"fuelL\":2.388,\"fullThrottlePct\":55.3,\"measuredTimeS\":93.18}",
            ToJson(in r));
    }

    [Fact]
    public void Nan_fields_are_null()
    {
        var r = new DiagRecord { Seq = 1, WallTicksUtc = DateTime.UtcNow.Ticks, SessionTime = double.NaN, Event = DiagEvent.LapCompleted, Level = DiagLevel.Info,
            A = double.NaN, B = 1, C = double.PositiveInfinity, D = double.NaN };
        using var doc = JsonDocument.Parse(ToJson(in r));
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("st").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("timeS").ValueKind);
        Assert.Equal(1.0, root.GetProperty("fuelL").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("fullThrottlePct").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("measuredTimeS").ValueKind);
    }

    [Fact]
    public void Unregistered_event_falls_back_to_generic_names()
    {
        var r = new DiagRecord { Seq = 1, WallTicksUtc = DateTime.UtcNow.Ticks, SessionTime = 1, Event = (DiagEvent)998, Level = DiagLevel.Warn, I0 = 3, A = 2 };
        using var doc = JsonDocument.Parse(ToJson(in r));
        Assert.Equal("998", doc.RootElement.GetProperty("ev").GetString());
        Assert.Equal("warn", doc.RootElement.GetProperty("lvl").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("i0").GetInt32());
    }

    [Fact]
    public void Catalog_register_adds_app_events()
    {
        var ev = (DiagEvent)1500;
        DiagEventCatalog.Register(ev, new DiagEventInfo("HotkeyPressed", "user", "key", null, null, null, null, null));
        Assert.Equal("HotkeyPressed", DiagEventCatalog.Get(ev).Name);
    }

    [Fact]
    public void Every_core_event_is_registered_with_a_category()
    {
        foreach (var e in Enum.GetValues<DiagEvent>())
        {
            var info = DiagEventCatalog.Get(e);
            Assert.Equal(e.ToString(), info.Name);
            Assert.Contains(info.Category, new[] { "telemetry", "laps", "stint", "leader", "strategy", "capture", "storage", "perf", "user" });
        }
    }

    [Fact]
    public void Text_and_exception_are_queued()
    {
        var log = new DiagLog(DiagLevel.Info);
        log.Text(DiagLevel.Info, "storage", "saved ok");
        log.Text(DiagLevel.Debug, "storage", "ignored");
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { log.Exception("storage", ex); }

        Assert.True(log.TryDequeueText(out var t1));
        Assert.Equal("storage", t1.Category);
        Assert.Equal("saved ok", t1.Message);
        Assert.Equal(DiagLevel.Info, t1.Level);
        Assert.True(log.TryDequeueText(out var t2));
        Assert.Equal(DiagLevel.Error, t2.Level);
        Assert.Contains("InvalidOperationException", t2.Message);
        Assert.Contains("boom", t2.Message);
        Assert.Contains("Text_and_exception_are_queued", t2.Message);
        Assert.False(log.TryDequeueText(out _));

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) DiagJsonFormatter.Write(w, in t2);
        using var doc = JsonDocument.Parse(ms.ToArray());
        Assert.Equal("error", doc.RootElement.GetProperty("lvl").GetString());
        Assert.Equal("storage", doc.RootElement.GetProperty("cat").GetString());
        Assert.Contains("boom", doc.RootElement.GetProperty("msg").GetString());
    }
}
