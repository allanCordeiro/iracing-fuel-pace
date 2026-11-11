using FuelPace.Core.Capture;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Capture;

public class CaptureSessionTests
{
    private static readonly CaptureConditions Conditions = new(30, 22, 1, 0, 0, LapSim.TankCapacityL);
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 20, 3, 40, TimeSpan.Zero);

    private static CaptureSession NewSession(DiagLog? log = null) => new(new FixedTimeProvider(T0), log);

    private static void Start(CaptureSession s, double sessionTime) => s.Start(sessionTime, "car__track", "Practice", Conditions);

    /// <summary>Drives the frames; capture starts on the first frame with SessionTime ≥ startAt, stops on the first ≥ stopAt.</summary>
    private static (CaptureRecord? record, LapDetector detector) Drive(CaptureSession s, IEnumerable<TelemetryFrame> frames,
        double startAt, double stopAt = double.PositiveInfinity)
    {
        var det = new LapDetector(s);
        det.SetTrackLength(LapSim.TrackLengthM);
        CaptureRecord? rec = null;
        bool started = false;
        foreach (var f in frames)
        {
            if (!started && f.SessionTime >= startAt) { started = true; Start(s, f.SessionTime); }
            if (started && rec is null && f.SessionTime >= stopAt) rec = s.Stop(false, default);
            det.Update(in f);
        }
        det.Flush();
        return (rec, det);
    }

    private static CompletedLap Lap(int n, double start, LapReasons reasons = LapReasons.None, double timeS = 80, double fuel = 2.4) =>
        new()
        {
            LapNumber = n, StartTime = start, EndTime = start + timeS, TimeS = timeS, MeasuredTimeS = timeS,
            FuelUsedL = fuel, FullThrottlePct = 65, Reasons = reasons,
        };

    private static CapturedLap Captured(int n, double? timeS, LapReasons reasons = LapReasons.None) =>
        new() { Lap = n, TimeS = timeS, FuelL = 2.4, FullThrottlePct = 65, Reasons = reasons };

    [Fact]
    public void Lap_in_progress_at_start_is_incomplete()
    {
        var s = NewSession();
        Drive(s, new LapSim().Run(laps: 3, endPct: 0.1), startAt: 120);   // mid lap 2 (80..160)
        var rec = s.Stop(false, default);

        Assert.Equal([2, 3, 4], rec.Laps.Select(l => l.Lap));   // lap 4: flushed in progress
        Assert.Equal(LapReasons.Incomplete, rec.Laps[0].Reasons);   // the detector saw it whole, the capture did not
        Assert.Equal(LapReasons.None, rec.Laps[1].Reasons);
        Assert.True(rec.Laps[1].Valid);
        Assert.Equal(80, rec.Laps[1].TimeS!.Value, 0.01);
        Assert.Equal(2.4, rec.Laps[1].FuelL, 0.001);
        Assert.Equal(LapReasons.Incomplete, rec.Laps[2].Reasons);
    }

    [Fact]
    public void Lap_in_progress_at_stop_is_recorded_incomplete()
    {
        var s = NewSession();
        var det = new LapDetector(s);
        det.SetTrackLength(LapSim.TrackLengthM);
        bool started = false;
        foreach (var f in new LapSim().Run(laps: 2, endPct: 0.5))
        {
            if (!started) { started = true; Start(s, f.SessionTime); }
            det.Update(in f);
        }
        Assert.True(det.TrySnapshotInProgress(out var snap));
        var rec = s.Stop(true, in snap);

        Assert.False(s.IsActive);
        Assert.Equal([1, 2, 3], rec.Laps.Select(l => l.Lap));
        Assert.Equal(LapReasons.None, rec.Laps[1].Reasons);
        var last = rec.Laps[^1];
        Assert.Equal(LapReasons.Incomplete, last.Reasons);
        Assert.False(last.Valid);
        Assert.Null(last.TimeS);
        Assert.Equal(1.2, last.FuelL, 0.1);
    }

    [Fact]
    public void Laps_outside_capture_window_are_ignored()
    {
        var s = NewSession();
        // Start 0.5 s after lap 2 crosses the line (t = 160): lap 2 ends before the window although its
        // official time only comes in later, and lap 3 started just before it. Stop mid lap 5 (320..400);
        // laps 5 and 6 come after the stop.
        var (rec, _) = Drive(s, new LapSim().Run(laps: 6, endPct: 0.1), startAt: 160.5, stopAt: 350);

        Assert.NotNull(rec);
        Assert.Equal([3, 4], rec.Laps.Select(l => l.Lap));
        Assert.Equal(LapReasons.Incomplete, rec.Laps[0].Reasons);
        Assert.Equal(LapReasons.None, rec.Laps[1].Reasons);
        Assert.Equal(2, s.TotalCount);   // nothing added after Stop
    }

    [Fact]
    public void Inactive_session_ignores_laps()
    {
        var s = NewSession();
        s.OnLapCompleted(Lap(1, 0));
        Assert.False(s.IsActive);
        Assert.Equal(0, s.TotalCount);
    }

    [Fact]
    public void Counts_track_valid_and_total()
    {
        var s = NewSession();
        Start(s, 0);
        Assert.True(s.IsActive);
        Assert.Equal("car__track", s.ProfileKey);
        s.OnLapCompleted(Lap(1, 0));
        s.OnLapCompleted(Lap(2, 80, LapReasons.Incident));
        s.OnLapCompleted(Lap(3, 160));
        s.OnLapCompleted(Lap(4, 240));
        Assert.Equal(3, s.ValidCount);
        Assert.Equal(4, s.TotalCount);

        var rec = s.Stop(false, default);
        Assert.Equal(4, rec.Laps.Count);
        Assert.Equal(3, rec.Laps.Count(l => l.Valid));
    }

    [Fact]
    public void Outlier_rule_flags_slow_otherwise_valid_laps()
    {
        var laps = new List<CapturedLap>
        {
            Captured(1, 80), Captured(2, 80.2), Captured(3, 79.9), Captured(4, 80.1), Captured(5, 90),
            Captured(6, 95, LapReasons.Incident),
        };
        OutlierRule.Apply(laps);
        Assert.Equal(LapReasons.Outlier, laps[4].Reasons);
        Assert.All(laps.Take(4), l => Assert.Equal(LapReasons.None, l.Reasons));
        Assert.Equal(LapReasons.Incident, laps[5].Reasons);

        OutlierRule.Apply(laps);   // idempotent: Outlier is recomputed, not compounded
        Assert.Equal(LapReasons.Outlier, laps[4].Reasons);
        Assert.All(laps.Take(4), l => Assert.Equal(LapReasons.None, l.Reasons));
        Assert.Equal(LapReasons.Incident, laps[5].Reasons);

        // Fewer than 2 other valid laps: nothing is flagged (and a stale Outlier is cleared).
        var few = new List<CapturedLap> { Captured(1, 80), Captured(2, 100, LapReasons.Outlier), Captured(3, 95, LapReasons.Pit), Captured(4, null) };
        OutlierRule.Apply(few);
        Assert.Equal(LapReasons.None, few[0].Reasons);
        Assert.Equal(LapReasons.None, few[1].Reasons);
        Assert.Equal(LapReasons.Pit, few[2].Reasons);
        Assert.Equal(LapReasons.None, few[3].Reasons);
    }

    [Fact]
    public void Stop_applies_outlier_rule()
    {
        var s = NewSession();
        Start(s, 0);
        double t = 0;
        foreach (var (n, time) in new[] { (1, 80.0), (2, 80.2), (3, 79.9), (4, 90.0) })
        {
            s.OnLapCompleted(Lap(n, t, timeS: time));
            t += time;
        }
        var rec = s.Stop(false, default);
        Assert.Equal(LapReasons.Outlier, rec.Laps[3].Reasons);
        Assert.Equal(3, rec.Laps.Count(l => l.Valid));
    }

    [Fact]
    public void Id_is_utc_iso_of_start()
    {
        var s = new CaptureSession(new FixedTimeProvider(new DateTimeOffset(2026, 10, 29, 17, 3, 40, TimeSpan.FromHours(-3))));
        Start(s, 4264);
        var rec = s.Stop(false, default);
        Assert.Equal("2026-10-29T20:03:40Z", rec.Id);
        Assert.True(rec.Active);
        Assert.Equal("Practice", rec.SessionType);
        Assert.Equal(Conditions, rec.Conditions);
        Assert.Empty(rec.Laps);
    }

    [Fact]
    public void CapturedLap_rounds_values_and_maps_missing_time_to_null()
    {
        var l = Lap(7, 0, LapReasons.OffTrack, timeS: 80.12345, fuel: 2.3876543);
        l.FullThrottlePct = 63.6789;
        l.HasProfile = true;
        for (int k = 0; k <= 100; k++) l.FuelProfile[k] = k / 300f;
        var c = CapturedLap.From(in l);

        Assert.Equal(7, c.Lap);
        Assert.Equal(80.123, c.TimeS);
        Assert.Equal(2.3877, c.FuelL);
        Assert.Equal(LapReasons.OffTrack, c.Reasons);
        Assert.False(c.Valid);
        Assert.NotNull(c.FuelProfile);
        Assert.Equal(101, c.FuelProfile.Length);
        Assert.Equal(0.0033f, c.FuelProfile[1]);
        Assert.Equal(0.3333f, c.FuelProfile[100]);

        l.TimeS = double.NaN;
        l.HasProfile = false;
        var noTime = CapturedLap.From(in l);
        Assert.Null(noTime.TimeS);
        Assert.Null(noTime.FuelProfile);
    }

    [Fact]
    public void Emits_CaptureLap_diag_for_each_recorded_lap()
    {
        var log = new DiagLog(DiagLevel.Info);
        var s = NewSession(log);
        s.OnLapCompleted(Lap(1, -100));   // inactive: no event
        Start(s, 0);
        s.OnLapCompleted(Lap(2, 0, LapReasons.Incident, timeS: 81.5, fuel: 2.39));
        var inProgress = Lap(3, 81.5, timeS: double.NaN, fuel: 1.1);
        inProgress.EndTime = 120;
        s.Stop(true, in inProgress);

        var buf = new DiagRecord[64];
        var recs = buf.AsSpan(0, log.DrainRecords(buf)).ToArray().Where(r => r.Event == DiagEvent.CaptureLap).ToArray();
        Assert.Equal(2, recs.Length);
        Assert.Equal(DiagLevel.Info, recs[0].Level);
        Assert.Equal(2, recs[0].I0);
        Assert.Equal((int)LapReasons.Incident, recs[0].I1);
        Assert.Equal(2.39, recs[0].A, 1e-9);
        Assert.Equal(81.5, recs[0].B, 1e-9);
        Assert.Equal(3, recs[1].I0);
        Assert.Equal((int)LapReasons.Incomplete, recs[1].I1);
        Assert.True(double.IsNaN(recs[1].B));
    }
}
