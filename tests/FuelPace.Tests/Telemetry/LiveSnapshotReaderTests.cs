using System.Buffers.Binary;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Telemetry;

[Collection(FuelPace.Tests.Perf.PerfCollection.Name)]
public class LiveSnapshotReaderTests
{
    static readonly VarSpec[] Vars =
    [
        new("SessionTime", VarType.Double), new("Lap", VarType.Int), new("FuelLevel", VarType.Float, 1, "l"),
    ];

    static double[] Row(double t, int lap, float fuel) => [t, lap, fuel];

    static byte[] Image(int t0, int t1, int t2, string yaml = "---\nA: 1\n...\n", int status = 1, int update = 1) =>
        IbtFileBuilder.BuildLiveImage(yaml, Vars, 3,
            [(t0, Row(t0, 1, 10f)), (t1, Row(t1, 2, 11f)), (t2, Row(t2, 3, 12f))], status, update);

    /// <summary>Connects (consumes the LayoutChanged read).</summary>
    static LiveSnapshotReader Connected(byte[] image, DiagLog? log = null)
    {
        var r = new LiveSnapshotReader(64 * 1024, log);
        var f = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.LayoutChanged, r.TryRead(image, ref f));
        return r;
    }

    [Fact]
    public void Picks_buffer_with_highest_tick()
    {
        var img = Image(10, 12, 11);
        var r = Connected(img);
        var f = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.Frame, r.TryRead(img, ref f));
        Assert.Equal(12.0, f.SessionTime);
        Assert.Equal(2, f.Lap);
        Assert.Equal(11f, f.FuelLevel);
    }

    [Fact]
    public void Same_tick_twice_is_no_new_data()
    {
        var img = Image(10, 12, 11);
        var r = Connected(img);
        var f = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.Frame, r.TryRead(img, ref f));
        Assert.Equal(LiveReadStatus.NoNewData, r.TryRead(img, ref f));
    }

    [Fact]
    public void Disconnected_status_is_reported()
    {
        var img = Image(10, 12, 11, status: 0);
        var r = new LiveSnapshotReader();
        var f = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.NotConnected, r.TryRead(img, ref f));
        Assert.Null(r.Layout);
        // an even status word with other bits set is still disconnected
        Assert.Equal(LiveReadStatus.NotConnected, r.TryRead(Image(10, 12, 11, status: 2), ref f));
    }

    [Fact]
    public void Torn_read_is_discarded_and_logged()
    {
        var log = new DiagLog(DiagLevel.Debug);
        var img = Image(10, 12, 11);
        var r = Connected(img, log);
        var f = new TelemetryFrame { SessionTime = -1 };
        // the producer rotates into the buffer being read: its tickCount (buf1, header offset 64) changes after our copy
        r.AfterCopyForTests = () => BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(64), 15);
        Assert.Equal(LiveReadStatus.TornRead, r.TryRead(img, ref f));
        Assert.Equal(-1, f.SessionTime); // frame untouched
        r.AfterCopyForTests = null;
        Assert.Equal(LiveReadStatus.Frame, r.TryRead(img, ref f)); // tick 15 is now the newest
        Assert.Equal(12.0, f.SessionTime); // (the row itself was not rewritten in this test)

        var buf = new DiagRecord[16];
        int n = log.DrainRecords(buf);
        Assert.Contains(buf.Take(n), d => d.Event == DiagEvent.TornRead && d.I0 == 1);
    }

    [Fact]
    public void Layout_change_is_detected_on_reconnect()
    {
        var first = Image(10, 12, 11);
        var r = Connected(first);
        var f = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.Frame, r.TryRead(first, ref f));
        var layoutBefore = r.Layout;
        Assert.Equal(0, layoutBefore!.MissingCount - (TelemetryVars.All.Count - 3));

        VarSpec[] other = [new("FuelLevel", VarType.Float, 1, "l"), new("Lap", VarType.Int), new("SessionTime", VarType.Double), new("Speed", VarType.Float)];
        var second = IbtFileBuilder.BuildLiveImage("---\nB: 1\n...\n", other, 3,
            [(5, [3f, 7, 99.5, 40f]), (4, [0, 0, 0, 0]), (3, [0, 0, 0, 0])]);
        Assert.Equal(LiveReadStatus.LayoutChanged, r.TryRead(second, ref f));
        Assert.NotSame(layoutBefore, r.Layout);
        Assert.Equal(LiveReadStatus.Frame, r.TryRead(second, ref f));
        Assert.Equal(99.5, f.SessionTime);
        Assert.Equal(7, f.Lap);
        Assert.Equal(3f, f.FuelLevel);
        Assert.Equal(40f, f.Speed);
    }

    [Fact]
    public void Reconnect_with_same_shape_reports_layout_changed_again()
    {
        var img = Image(10, 12, 11);
        var r = Connected(img);
        var f = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.NotConnected, r.TryRead(Image(10, 12, 11, status: 0), ref f));
        // iRacing restarted: tick counts start over, so the same tick as before must not read as "no new data"
        Assert.Equal(LiveReadStatus.LayoutChanged, r.TryRead(img, ref f));
        Assert.Equal(LiveReadStatus.Frame, r.TryRead(img, ref f));
    }

    [Fact]
    public void Session_info_update_counter_is_exposed()
    {
        var v1 = Image(10, 12, 11, yaml: "---\nCar: one\n...\n", update: 1);
        var v2 = Image(10, 12, 11, yaml: "---\nCar: two\n...\n", update: 2);
        var r = Connected(v1);
        Assert.Equal(1, r.SessionInfoUpdate);
        Assert.Equal("---\nCar: one\n...\n", r.ReadSessionInfoYaml(v1));
        var f = new TelemetryFrame();
        r.TryRead(v2, ref f);
        Assert.Equal(2, r.SessionInfoUpdate);
        Assert.Equal("---\nCar: two\n...\n", r.ReadSessionInfoYaml(v2));
    }

    [Fact]
    public void Yaml_is_latin1_and_stops_at_nul()
    {
        // stale bytes after the terminator (the YAML is rewritten in place and may shrink) must not leak through
        var img = Image(10, 12, 11, yaml: "---\nDriver: Jos\u00e9 M\u00fcller\n...\n\0stale: tail");
        var text = new LiveSnapshotReader().ReadSessionInfoYaml(img);
        Assert.Equal("---\nDriver: Jos\u00e9 M\u00fcller\n...\n", text);
    }

    [Fact]
    public void Truncated_or_invalid_memory_is_not_connected()
    {
        var r = new LiveSnapshotReader();
        var f = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.NotConnected, r.TryRead(new byte[50], ref f));
        var img = Image(10, 12, 11);
        Assert.Equal(LiveReadStatus.NotConnected, r.TryRead(img.AsSpan(0, 150), ref f));
        Assert.Equal("", r.ReadSessionInfoYaml(new byte[10]));
    }

    [Fact]
    public void TryRead_allocates_nothing_after_connect()
    {
        var img = Image(10, 12, 11);
        var r = Connected(img);
        var f = new TelemetryFrame();
        for (int i = 0; i < 100; i++) { SetTicks(img, 100 + i); r.TryRead(img, ref f); } // warm up
        int frames = 0;
        var window = AllocationMeasure.Start();
        for (int i = 0; i < 10_000; i++)
        {
            SetTicks(img, 1000 + i / 2); // every second read repeats the tick -> NoNewData
            if (r.TryRead(img, ref f) == LiveReadStatus.Frame) frames++;
        }
        window.AssertNone();
        Assert.Equal(5000, frames);
    }

    static void SetTicks(byte[] img, int tick)
    {
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(48), tick - 2);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(64), tick);
        BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(80), tick - 1);
    }
}
