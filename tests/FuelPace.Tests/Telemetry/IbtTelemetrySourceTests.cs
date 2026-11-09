using System.Diagnostics;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Telemetry;

[Collection(FuelPace.Tests.Perf.PerfCollection.Name)]
public class IbtTelemetrySourceTests
{
    private const string Yaml = "---\nWeekendInfo:\n TrackName: test\n...\n";

    private static TelemetryFrame Frame(double t, float pct, float fuel)
    {
        var f = new TelemetryFrame
        {
            SessionTime = t, SessionTimeRemain = 600.5, SessionNum = 2, SessionState = 4, SessionLapsRemainEx = 20,
            Lap = 5, LapCompleted = 4, PlayerTrackSurface = 3, PlayerCarMyIncidentCount = 2, PlayerCarIdx = 7,
            TrackWetness = 1, SessionFlags = 0x4000, LapDistPct = pct, LapLastLapTime = 92.25f, FuelLevel = fuel,
            FuelLevelPct = 0.5f, FuelUsePerHour = 30f, PlayerCarTowTime = 0f, Speed = 50f, Throttle = 0.75f,
            TrackTempCrew = 30f, AirTemp = 20f, PlayerCarPowerAdjust = 1f, PlayerCarWeightPenalty = 2f,
            IsOnTrack = true,
        };
        f.CarIdxPosition[7] = 1;
        f.CarIdxLapCompleted[3] = 4;
        f.CarIdxTrackSurface[3] = 3;
        f.CarIdxClass[3] = 4098;
        f.CarIdxLapDistPct[3] = 0.25f;
        f.CarIdxLastLapTime[7] = 99.5f;
        f.CarIdxOnPitRoad[7] = true;
        return f;
    }

    private static List<TelemetryFrame> Synthetic(int n, double dt)
    {
        var l = new List<TelemetryFrame>(n);
        for (int i = 0; i < n; i++) l.Add(Frame(1.0 + i * dt, (float)((i * 0.0001) % 1), 50f - i * 0.001f));
        return l;
    }

    [Fact]
    public void Round_trips_session_flags_with_bit_31_set()
    {
        var frame = Frame(1.0, 0.1f, 50f);
        frame.SessionFlags = 0x80004000u;
        var path = Fixtures.TempPath(".ibt");
        try
        {
            IbtFileBuilder.WriteFrames(path, Yaml, [frame]);
            using var src = new IbtTelemetrySource(path);
            var f = new TelemetryFrame();
            Assert.Equal(ReadResult.Frame, src.TryRead(ref f));
            Assert.Equal(0x80004000u, f.SessionFlags);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Round_trips_scalar_and_caridx_fields()
    {
        var expected = new[] { Frame(1.0, 0.1f, 50f), Frame(1.0167, 0.2f, 49.99f), Frame(1.0333, 0.3f, 49.98f) };
        expected[1].OnPitRoad = true;
        var path = Fixtures.TempPath(".ibt");
        try
        {
            IbtFileBuilder.WriteFrames(path, Yaml, expected);
            using var src = new IbtTelemetrySource(path);
            Assert.True(src.Layout.HasCarIdx);
            Assert.Equal(Yaml, src.ReadSessionInfoYaml());
            Assert.Equal(1, src.SessionInfoVersion);
            var f = new TelemetryFrame();
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(ReadResult.Frame, src.TryRead(ref f));
                var e = expected[i];
                Assert.Equal(e.SessionTime, f.SessionTime);
                Assert.Equal(e.SessionTimeRemain, f.SessionTimeRemain);
                Assert.Equal(e.SessionNum, f.SessionNum);
                Assert.Equal(e.SessionState, f.SessionState);
                Assert.Equal(e.SessionLapsRemainEx, f.SessionLapsRemainEx);
                Assert.Equal(5, f.Lap);
                Assert.Equal(4, f.LapCompleted);
                Assert.Equal(3, f.PlayerTrackSurface);
                Assert.Equal(2, f.PlayerCarMyIncidentCount);
                Assert.Equal(7, f.PlayerCarIdx);
                Assert.Equal(1, f.TrackWetness);
                Assert.Equal(0x4000u, f.SessionFlags);
                Assert.Equal(e.LapDistPct, f.LapDistPct);
                Assert.Equal(e.LapLastLapTime, f.LapLastLapTime);
                Assert.Equal(e.FuelLevel, f.FuelLevel);
                Assert.Equal(0.5f, f.FuelLevelPct);
                Assert.Equal(30f, f.FuelUsePerHour);
                Assert.Equal(50f, f.Speed);
                Assert.Equal(0.75f, f.Throttle);
                Assert.Equal(30f, f.TrackTempCrew);
                Assert.Equal(20f, f.AirTemp);
                Assert.Equal(1f, f.PlayerCarPowerAdjust);
                Assert.Equal(2f, f.PlayerCarWeightPenalty);
                Assert.Equal(i == 1, f.OnPitRoad);
                Assert.False(f.PlayerCarInPitStall);
                Assert.True(f.IsOnTrack);
                Assert.False(f.IsReplayPlaying);
                Assert.True(f.HasCarIdx);
                Assert.Equal(1, f.CarIdxPosition[7]);
                Assert.Equal(0, f.CarIdxPosition[6]);
                Assert.Equal(4, f.CarIdxLapCompleted[3]);
                Assert.Equal(3, f.CarIdxTrackSurface[3]);
                Assert.Equal(4098, f.CarIdxClass[3]);
                Assert.Equal(0.25f, f.CarIdxLapDistPct[3]);
                Assert.Equal(99.5f, f.CarIdxLastLapTime[7]);
                Assert.True(f.CarIdxOnPitRoad[7]);
                Assert.False(f.CarIdxOnPitRoad[8]);
            }
            Assert.Equal(ReadResult.EndOfStream, src.TryRead(ref f));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_caridx_vars_yield_HasCarIdx_false_and_zeroed_arrays()
    {
        var path = Fixtures.TempPath(".ibt");
        try
        {
            IbtFileBuilder.WriteFrames(path, Yaml, [Frame(1, 0.1f, 50f)], includeCarIdx: false);
            using var src = new IbtTelemetrySource(path);
            Assert.False(src.Layout.HasCarIdx);
            Assert.False(src.Layout.Has(TelemetryVarId.CarIdxPosition));
            Assert.Equal(-1, src.Layout[TelemetryVarId.CarIdxPosition].Offset);
            // dirty the arrays first: the reader must zero what is absent
            var f = new TelemetryFrame { HasCarIdx = true };
            f.CarIdxPosition[7] = 9;
            f.CarIdxLastLapTime[7] = 1f;
            f.CarIdxOnPitRoad[7] = true;
            Assert.Equal(ReadResult.Frame, src.TryRead(ref f));
            Assert.False(f.HasCarIdx);
            Assert.Equal(0, f.CarIdxPosition[7]);
            Assert.Equal(0f, f.CarIdxLastLapTime[7]);
            Assert.False(f.CarIdxOnPitRoad[7]);
            Assert.Equal(50f, f.FuelLevel);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("kWh", true)]
    [InlineData("KWH", true)]
    [InlineData("l", false)]
    public void Electric_unit_is_detected(string unit, bool electric)
    {
        var path = Fixtures.TempPath(".ibt");
        try
        {
            IbtFileBuilder.WriteFrames(path, Yaml, [Frame(1, 0.1f, 50f)], fuelUnit: unit);
            using var src = new IbtTelemetrySource(path);
            Assert.Equal(unit, src.Layout.FuelUnit);
            Assert.Equal(electric, src.Layout.IsElectric);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reading_frames_allocates_nothing_after_first_read()
    {
        var path = Fixtures.TempPath(".ibt");
        try
        {
            IbtFileBuilder.WriteFrames(path, Yaml, Synthetic(3000, 1.0 / 60));
            using var src = new IbtTelemetrySource(path);
            var f = new TelemetryFrame();
            for (int i = 0; i < 1000; i++) Assert.Equal(ReadResult.Frame, src.TryRead(ref f)); // warm-up: JIT and first-call stubs
            var window = AllocationMeasure.Start();
            for (int i = 0; i < 1999; i++)
                if (src.TryRead(ref f) != ReadResult.Frame) Assert.Fail("unexpected end");
            window.AssertNone();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Paced_replay_does_not_drift()
    {
        var path = Fixtures.TempPath(".ibt");
        try
        {
            IbtFileBuilder.WriteFrames(path, Yaml, Synthetic(120, 1.0 / 60));
            using var src = new IbtTelemetrySource(path, ReplaySpeed.X10);
            var f = new TelemetryFrame();
            var sw = Stopwatch.StartNew();
            while (src.TryRead(ref f) == ReadResult.Frame) { }
            sw.Stop();
            // 119 intervals / 60 Hz / 10 = 0.198 s. The upper bound only has to separate "absolute deadline" from drift: sleeping
            // a relative interval per frame costs >= 119 x 15 ms = 1.8 s on Windows, so 0.60 s still catches it while a loaded
            // CI host (one descheduled sleep) no longer fails the test.
            Assert.InRange(sw.Elapsed.TotalSeconds, 0.19, 0.60);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Fixture_first_frame_is_mid_session_mid_lap()
    {
        var path = Fixtures.RequireZolder();
        using var src = new IbtTelemetrySource(path);
        var f = new TelemetryFrame();
        Assert.Equal(ReadResult.Frame, src.TryRead(ref f));
        Assert.InRange(f.SessionTime, 4263, 4265);
        Assert.InRange(f.LapDistPct, 0.062, 0.072);
        Assert.InRange(f.FuelLevel, 90.0001f, 100f);
        Assert.False(src.Layout.HasCarIdx);
        Assert.False(src.Layout.IsElectric);
    }
}
