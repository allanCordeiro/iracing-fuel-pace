using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Laps;

public class LapDetectorTests
{
    private sealed class Sink : ILapSink
    {
        public readonly List<CompletedLap> Laps = [];
        public readonly List<double> EmittedAt = [];
        public double Now;
        public void OnLapCompleted(in CompletedLap lap) { Laps.Add(lap); EmittedAt.Add(Now); }
        public int IndexOf(int lapNumber) => Laps.FindIndex(l => l.LapNumber == lapNumber);
        public CompletedLap Lap(int lapNumber) => Laps[IndexOf(lapNumber)];
    }

    private sealed class CountingSink : ILapSink
    {
        public int Count;
        public void OnLapCompleted(in CompletedLap lap) => Count++;
    }

    private static Sink Drive(IEnumerable<TelemetryFrame> frames, bool flush = false, DiagLog? log = null,
        Action<LapDetector, TelemetryFrame>? afterEach = null)
    {
        var sink = new Sink();
        var det = new LapDetector(sink, log);
        det.SetTrackLength(LapSim.TrackLengthM);
        foreach (var f in frames)
        {
            sink.Now = f.SessionTime;
            det.Update(in f);
            afterEach?.Invoke(det, f);
        }
        if (flush) det.Flush();
        return sink;
    }

    [Fact]
    public void Wrap_from_pct_at_or_past_one_puts_the_line_on_the_previous_frame()
    {
        // iRacing can report LapDistPct = 1.0000x on the last frame before it wraps (seen in the Zolder fixture).
        var frames = new LapSim().Run(laps: 3, endPct: 0.1).ToArray();
        int wrap = Array.FindIndex(frames, f => f.Lap == 3);
        frames[wrap - 1].LapDistPct = 1.000003f;
        var log = new DiagLog(DiagLevel.Debug);
        var sink = Drive(frames, log: log);

        var lap2 = sink.Lap(2);
        Assert.Equal(frames[wrap - 1].SessionTime, lap2.EndTime, 1e-9);
        Assert.Equal(lap2.EndTime, sink.Lap(3).StartTime);
        var buf = new DiagRecord[256];
        var boundary = Assert.Single(buf.AsSpan(0, log.DrainRecords(buf)).ToArray(),
            r => r.Event == DiagEvent.LapBoundary && r.I0 == 2);
        Assert.Equal(0, boundary.C);
    }

    [Fact]
    public void Emits_complete_laps_with_interpolated_fuel_and_time()
    {
        var sink = Drive(new LapSim(startPct: 0.5, startLap: 1).Run(laps: 3, endPct: 0.1));

        var first = sink.Laps[0];
        Assert.Equal(1, first.LapNumber);
        Assert.False(first.Stats.StartObserved);
        Assert.Equal(LapReasons.Incomplete, first.Reasons);

        Assert.Equal([1, 2, 3], sink.Laps.Select(l => l.LapNumber));
        foreach (var n in new[] { 2, 3 })
        {
            var l = sink.Lap(n);
            Assert.Equal(2.4, l.FuelUsedL, 0.001);
            Assert.Equal(80, l.MeasuredTimeS, 0.02);
            Assert.Equal(80, l.TimeS, 0.001);
            Assert.Equal(LapReasons.None, l.Reasons);
            Assert.True(l.Stats.StartObserved && l.Stats.EndObserved);
            Assert.True(l.HasProfile);
        }
    }

    [Fact]
    public void Waits_for_delayed_LapLastLapTime()
    {
        var sink = Drive(new LapSim().LapTimes(80, 81.234).Run(3, endPct: 0.1));

        var lap2 = sink.Lap(2);
        Assert.Equal(81.234, lap2.TimeS, 0.001);
        Assert.Equal(81.234, lap2.MeasuredTimeS, 0.02);
        double at = sink.EmittedAt[sink.IndexOf(2)];
        Assert.InRange(at, lap2.EndTime + 1.5 - 1e-3, lap2.EndTime + 1.5 + 1.0 / 60 + 1e-3);
    }

    [Fact]
    public void Falls_back_to_measured_time_after_5s()
    {
        // The first official time only appears at 80 + 99 s, so laps 1 and 2 time out with LapLastLapTime = -1.
        var sink = Drive(new LapSim(officialTimeDelayS: 99).Run(4, endPct: 0.1));

        Assert.True(double.IsNaN(sink.Lap(1).TimeS));
        var lap3 = sink.Lap(3);
        Assert.Equal(lap3.MeasuredTimeS, lap3.TimeS, 0.001);
        double at = sink.EmittedAt[sink.IndexOf(3)];
        Assert.InRange(at, lap3.EndTime + 5 - 1e-6, lap3.EndTime + 5 + 1.0 / 60 + 1e-6);
    }

    [Fact]
    public void Identical_consecutive_official_times_resolve_by_timeout()
    {
        // LapLastLapTime goes 80 -> 80 for lap 2: no change is visible, so the 5 s fallback applies.
        var sink = Drive(new LapSim().Run(2, endPct: 0.1));

        var lap2 = sink.Lap(2);
        Assert.Equal(lap2.MeasuredTimeS, lap2.TimeS, 1e-9);
        Assert.InRange(sink.EmittedAt[sink.IndexOf(2)], lap2.EndTime + 5 - 1e-6, lap2.EndTime + 5 + 1.0 / 60 + 1e-6);
    }

    [Fact]
    public void Non_positive_official_time_means_no_time()
    {
        var sink = Drive(new LapSim(publishOfficialTimes: false).Run(3), flush: true);

        Assert.True(sink.Laps.Count >= 3);
        foreach (var l in sink.Laps) Assert.True(double.IsNaN(l.TimeS));
        Assert.Equal(80, sink.Lap(2).MeasuredTimeS, 0.02);
    }

    [Fact]
    public void Fuel_profile_is_cumulative_fraction()
    {
        var sink = Drive(new LapSim().Run(2, endPct: 0.1));

        Assert.False(sink.Lap(1).HasProfile); // the run starts on the line, but no crossing was seen
        var l = sink.Lap(2);
        Assert.True(l.HasProfile);
        Assert.Equal(0f, l.FuelProfile[0]);
        Assert.Equal(1f, l.FuelProfile[100]);
        Assert.Equal(0.46, l.FuelProfile[50], 0.002);
        Assert.Equal(LapSim.FuelCurve(0.25), l.FuelProfile[25], 0.002);
        for (int k = 1; k <= 100; k++) Assert.True(l.FuelProfile[k] >= l.FuelProfile[k - 1], $"profile[{k}]");
    }

    [Fact]
    public void Flush_emits_in_progress_lap_as_incomplete()
    {
        var sink = Drive(new LapSim().Run(2, endPct: 0.4), flush: true);

        Assert.Equal([1, 2, 3], sink.Laps.Select(l => l.LapNumber));
        var last = sink.Laps[^1];
        Assert.False(last.Stats.EndObserved);
        Assert.True(last.Stats.StartObserved);
        Assert.Equal(LapReasons.Incomplete, last.Reasons);
        Assert.Equal(2.4 * LapSim.FuelCurve(0.4), last.FuelUsedL, 0.005);
        Assert.False(last.HasProfile);
        Assert.True(double.IsNaN(last.TimeS));
    }

    [Fact]
    public void Flush_resolves_pending_lap_by_timeout()
    {
        // Ends right at the line: lap 2 is still waiting for its official time.
        var sink = Drive(new LapSim().Run(2), flush: true);

        var lap2 = sink.Lap(2);
        Assert.Equal(lap2.MeasuredTimeS, lap2.TimeS, 1e-9);
        Assert.Equal(LapReasons.None, lap2.Reasons);
        Assert.Equal(3, sink.Laps[^1].LapNumber);
        Assert.False(sink.Laps[^1].Stats.EndObserved);
    }

    [Fact]
    public void Lap_number_is_Lap_value_at_mid_lap()
    {
        var sink = Drive(new LapSim().IncrementLapEarlyFrames(3).Run(3), flush: true);

        Assert.Equal([1, 2, 3, 4], sink.Laps.Select(l => l.LapNumber));
        Assert.All(sink.Laps.Take(3), l => Assert.False(l.Stats.ResetSeen));
        Assert.Equal(2.4, sink.Lap(2).FuelUsedL, 0.001);
    }

    [Fact]
    public void Full_throttle_pct_is_time_weighted()
    {
        var sink = Drive(new LapSim(fullThrottleFraction: 0.6).Run(2, endPct: 0.1));

        Assert.Equal(60, sink.Lap(1).FullThrottlePct, 1.0);
        Assert.Equal(60, sink.Lap(2).FullThrottlePct, 1.0);
    }

    [Fact]
    public void Lap_increment_without_wrap_ends_lap_with_reset()
    {
        // From the first frame of lap 2 with pct >= 0.4 on, Lap reads one more: increment with continuous pct.
        var frames = new LapSim().Run(3, endPct: 0.1).ToArray();
        int inc = Array.FindIndex(frames, f => f.Lap == 2 && f.LapDistPct >= 0.4f);
        for (int i = inc; i < frames.Length; i++) frames[i].Lap++;
        double incTime = frames[inc].SessionTime;

        var sink = Drive(frames, flush: true);

        var lap2 = sink.Lap(2);
        Assert.True(lap2.Stats.ResetSeen);
        Assert.True(lap2.Stats.EndObserved);
        Assert.InRange(lap2.EndTime, incTime + 1.0 - 1e-6, incTime + 1.0 + 1.0 / 60 + 1e-6);
        Assert.Equal(2.4 * LapSim.FuelCurve(lap2.EndTime / 80 - 1), lap2.FuelUsedL, 0.005);
        // the partial lap that starts at the increment is contaminated as well
        var next = sink.Laps[sink.IndexOf(2) + 1];
        Assert.Equal(3, next.LapNumber);
        Assert.True(next.Stats.ResetSeen);
        Assert.False(sink.Lap(1).Stats.ResetSeen);
    }

    [Fact]
    public void Current_lap_properties_track_progress_and_pit()
    {
        var hasPitByLap = new Dictionary<int, bool>();
        double lap2Start = double.NaN, fuelAtMid = double.NaN;
        var sink = Drive(new LapSim().PitRoad(2, 0.2, 0.3).Run(2, endPct: 0.1), afterEach: (d, f) =>
        {
            hasPitByLap[f.Lap] = hasPitByLap.GetValueOrDefault(f.Lap) | d.CurrentLapHasPit;
            if (f.Lap == 2 && double.IsNaN(lap2Start)) lap2Start = d.CurrentLapStartTime;
            if (f.Lap == 1 && double.IsNaN(fuelAtMid) && f.LapDistPct >= 0.5f)
            {
                Assert.Equal(f.LapDistPct, d.CurrentLapDistPct);
                fuelAtMid = d.CurrentLapFuelUsedL;
            }
        });

        Assert.False(hasPitByLap[1]);
        Assert.True(hasPitByLap[2]);
        Assert.False(hasPitByLap[3]);
        Assert.Equal(80, lap2Start, 0.001);
        Assert.Equal(2.4 * 0.46, fuelAtMid, 0.002);
        Assert.True(sink.Lap(2).Stats.PitSeen);
        Assert.False(sink.Lap(1).Stats.PitSeen);
    }

    [Fact]
    public void TrySnapshotInProgress_reports_current_lap_without_emitting()
    {
        var sink = new Sink();
        var det = new LapDetector(sink);
        Assert.False(det.TrySnapshotInProgress(out _));
        foreach (var f in new LapSim().Run(1, endPct: 0.5)) det.Update(in f);
        int emitted = sink.Laps.Count;

        Assert.True(det.TrySnapshotInProgress(out var snap));
        Assert.Equal(2, snap.LapNumber);
        Assert.False(snap.Stats.EndObserved);
        Assert.Equal(LapReasons.Incomplete, snap.Reasons);
        Assert.Equal(1.2 - 2.4 * 0.04, snap.FuelUsedL, 0.005);
        Assert.Equal(emitted, sink.Laps.Count);
    }

    [Fact]
    public void Emits_LapCompleted_diag_with_lap_fuel_and_time()
    {
        var log = new DiagLog(DiagLevel.Debug);
        var sink = Drive(new LapSim().LapTimes(80, 80.25).Run(2, endPct: 0.1), log: log);

        var buf = new DiagRecord[256];
        var recs = buf.AsSpan(0, log.DrainRecords(buf)).ToArray();
        Assert.Equal((int)LapReasons.Incomplete, Assert.Single(recs, r => r.Event == DiagEvent.LapCompleted && r.I0 == 1).I1);

        var lap2 = sink.Lap(2);
        var done = Assert.Single(recs, r => r.Event == DiagEvent.LapCompleted && r.I0 == 2);
        Assert.Equal(DiagLevel.Info, done.Level);
        Assert.Equal((int)LapReasons.None, done.I1);
        Assert.Equal(lap2.TimeS, done.A);
        Assert.Equal(lap2.FuelUsedL, done.B);
        Assert.Equal(lap2.FullThrottlePct, done.C);
        Assert.Equal(lap2.MeasuredTimeS, done.D);
        Assert.Equal(lap2.EndTime, done.SessionTime);
        Assert.Equal(80.25, done.A, 0.001);
        Assert.Equal(2.4, done.B, 0.001);

        var boundary = Assert.Single(recs, r => r.Event == DiagEvent.LapBoundary && r.I0 == 2);
        Assert.Equal(DiagLevel.Debug, boundary.Level);
        Assert.Equal(0, boundary.I1);
        Assert.InRange(boundary.C, 0, 1);
        var resolved = Assert.Single(recs, r => r.Event == DiagEvent.LapTimeResolved && r.I0 == 2);
        Assert.Equal(0, resolved.I1);
        Assert.Equal(1.5, resolved.B, 1.0 / 60 + 1e-3);
    }

    [Fact]
    public void Update_allocates_nothing_across_laps()
    {
        // Reason triggers fire in every lap (incident, offtrack, caution, pit, refuel, tow, time gap) so Trigger/Emit run hot.
        var frames = new LapSim(lapTimeS: 20, fuelPerLapL: 0.6)
            .Incident(2, 0.3).OffTrack(2, 0.4, 3).Caution(3, 0.2, 0.4).PitRoad(3, 0.5, 0.6)
            .Refuel(4, 0.5, 1.0).Tow(4, 0.7).TimeGap(5, 0.2, 3).Run(5, endPct: 0.3).ToArray();
        var log = new DiagLog(DiagLevel.Trace, capacity: 1 << 16);

        var warm = new LapDetector(new CountingSink(), log);
        for (int i = 0; i < frames.Length; i++) warm.Update(in frames[i]);
        warm.Flush();
        warm.TrySnapshotInProgress(out _);
        log.DrainRecords(new DiagRecord[1 << 16]);

        var sink = new CountingSink();
        var det = new LapDetector(sink, log);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames.Length; i++) det.Update(in frames[i]);
        det.TrySnapshotInProgress(out _);
        det.Flush();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.Equal(6, sink.Count);
        var recs = new DiagRecord[1 << 16];
        var n = log.DrainRecords(recs);
        Assert.True(recs.AsSpan(0, n).ToArray().Count(r => r.Event == DiagEvent.LapReasonTriggered) >= 6);
    }
}
