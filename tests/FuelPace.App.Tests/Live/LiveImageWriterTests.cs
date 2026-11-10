using System.Buffers.Binary;
using FuelPace.App.Live;
using FuelPace.App.Simulation;
using FuelPace.Core.Telemetry;
using static FuelPace.App.Tests.Recording.RecordingTestSupport;

namespace FuelPace.App.Tests.Live;

public class LiveImageWriterTests
{
    private const string Yaml1 = "---\nWeekendInfo:\n TrackName: zolder\n...\n";
    private const string Yaml2 = "---\nWeekendInfo:\n TrackName: spa\n...\n";

    private static (IrsdkLiveImageWriter w, byte[] image) Create(IReadOnlyList<VarSpec>? vars = null, int numBuf = 3)
    {
        var w = new IrsdkLiveImageWriter(vars ?? TelemetryVars.All, numBuf, yamlCapacity: 4096);
        var image = new byte[w.TotalSize];
        w.Initialize(image, Yaml1);
        return (w, image);
    }

    [Fact]
    public void Reader_gets_the_last_of_five_frames_in_three_buffers()
    {
        var (w, image) = Create();
        var frames = SyntheticRace.Frames(1, withCarIdx: true).Take(5).ToList();
        foreach (var f in frames) w.WriteFrame(image, f);

        var r = new LiveSnapshotReader();
        var got = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.LayoutChanged, r.TryRead(image, ref got));
        Assert.Equal(0, r.Layout!.MissingCount);
        Assert.True(r.Layout.HasCarIdx);
        Assert.Equal(LiveReadStatus.Frame, r.TryRead(image, ref got));
        AssertSameFrame(frames[4], got, 4);
        Assert.Equal(1, r.SessionInfoUpdate);
        Assert.Equal(Yaml1, r.ReadSessionInfoYaml(image));
        Assert.Equal(LiveReadStatus.NoNewData, r.TryRead(image, ref got));
    }

    [Fact]
    public void Every_frame_is_read_in_order_with_a_rising_tick_count()
    {
        var (w, image) = Create();
        var frames = SyntheticRace.Frames(1, withCarIdx: true).Take(10).ToList();
        var r = new LiveSnapshotReader();
        var got = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.LayoutChanged, r.TryRead(image, ref got));
        Assert.Equal(LiveReadStatus.NoNewData, r.TryRead(image, ref got)); // nothing written yet: no frame of zeros

        int lastTick = int.MinValue;
        for (int i = 0; i < frames.Count; i++)
        {
            w.WriteFrame(image, frames[i]);
            Assert.Equal(LiveReadStatus.Frame, r.TryRead(image, ref got));
            AssertSameFrame(frames[i], got, i);
            int tick = MaxTick(image, 3);
            Assert.True(tick > lastTick, $"tick {tick} after {lastTick}");
            lastTick = tick;
        }
    }

    [Fact]
    public void Session_info_update_increments_the_counter_and_rewrites_the_yaml()
    {
        var (w, image) = Create();
        w.WriteFrame(image, SyntheticRace.Frames(1, withCarIdx: false).First());
        w.UpdateSessionInfo(image, Yaml2);

        var r = new LiveSnapshotReader();
        var got = new TelemetryFrame();
        r.TryRead(image, ref got);
        Assert.Equal(2, r.SessionInfoUpdate);
        Assert.Equal(Yaml2, r.ReadSessionInfoYaml(image));

        w.UpdateSessionInfo(image, Yaml1); // shorter text over a longer one: the NUL ends it
        r.TryRead(image, ref got);
        Assert.Equal(3, r.SessionInfoUpdate);
        Assert.Equal(Yaml1, r.ReadSessionInfoYaml(image));
    }

    [Fact]
    public void Disconnected_image_reads_as_not_connected_and_reconnects()
    {
        var (w, image) = Create();
        var frames = SyntheticRace.Frames(1, withCarIdx: false).Take(3).ToList();
        w.WriteFrame(image, frames[0]);
        var r = new LiveSnapshotReader();
        var got = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.LayoutChanged, r.TryRead(image, ref got));

        w.SetConnected(image, false);
        Assert.Equal(LiveReadStatus.NotConnected, r.TryRead(image, ref got));

        w.SetConnected(image, true);
        w.WriteFrame(image, frames[1]);
        Assert.Equal(LiveReadStatus.LayoutChanged, r.TryRead(image, ref got));
        Assert.Equal(LiveReadStatus.Frame, r.TryRead(image, ref got));
        AssertSameFrame(frames[1], got, 1);
    }

    [Fact]
    public void A_subset_of_variables_with_other_native_types_is_written_by_name()
    {
        VarSpec[] vars =
        [
            new("SessionTime", VarType.Double), new("Lap", VarType.Int), new("FuelLevel", VarType.Float, 1, "l"),
            new("SessionFlags", VarType.BitField), new("OnPitRoad", VarType.Bool), new("NotInCatalog", VarType.Float),
            new("CarIdxLapDistPct", VarType.Float, 64),
        ];
        var (w, image) = Create(vars, numBuf: 2);
        var f = new TelemetryFrame { SessionTime = 12.5, Lap = 7, FuelLevel = 33.25f, SessionFlags = 0x8000_0004, OnPitRoad = true };
        f.CarIdxLapDistPct[0] = 0.25f;
        f.CarIdxLapDistPct[63] = 0.75f;
        w.WriteFrame(image, f);

        var r = new LiveSnapshotReader();
        var got = new TelemetryFrame();
        Assert.Equal(LiveReadStatus.LayoutChanged, r.TryRead(image, ref got));
        Assert.Equal("l", r.Layout!.FuelUnit);
        Assert.Equal(LiveReadStatus.Frame, r.TryRead(image, ref got));
        Assert.Equal(12.5, got.SessionTime);
        Assert.Equal(7, got.Lap);
        Assert.Equal(33.25f, got.FuelLevel);
        Assert.Equal(0x8000_0004u, got.SessionFlags);
        Assert.True(got.OnPitRoad);
        Assert.Equal(0.25f, got.CarIdxLapDistPct[0]);
        Assert.Equal(0.75f, got.CarIdxLapDistPct[63]);
    }

    [Fact]
    public void Arguments_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IrsdkLiveImageWriter(TelemetryVars.All, numBuf: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IrsdkLiveImageWriter(TelemetryVars.All, numBuf: 5));
        var w = new IrsdkLiveImageWriter(TelemetryVars.All, 3, yamlCapacity: 16);
        Assert.Throws<ArgumentException>(() => w.Initialize(new byte[w.TotalSize - 1], ""));
        Assert.Throws<ArgumentException>(() => w.Initialize(new byte[w.TotalSize], new string('x', 16))); // no room for the NUL
    }

    private static int MaxTick(byte[] image, int numBuf)
    {
        int max = int.MinValue;
        for (int i = 0; i < numBuf; i++) max = Math.Max(max, BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(48 + 16 * i)));
        return max;
    }
}
