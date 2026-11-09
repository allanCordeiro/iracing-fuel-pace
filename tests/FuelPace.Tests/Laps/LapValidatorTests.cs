using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Laps;

public class LapValidatorTests
{
    private sealed class Sink : ILapSink
    {
        public readonly List<CompletedLap> Laps = [];
        public void OnLapCompleted(in CompletedLap lap) => Laps.Add(lap);
        public CompletedLap Lap(int n) => Laps.Single(l => l.LapNumber == n);
        public LapReasons Reasons(int n) => Lap(n).Reasons;
    }

    private static Sink Drive(IEnumerable<TelemetryFrame> frames, DiagLog? log = null)
    {
        var sink = new Sink();
        var det = new LapDetector(sink, log);
        det.SetTrackLength(LapSim.TrackLengthM);
        foreach (var f in frames) det.Update(in f);
        det.Flush();
        return sink;
    }

    // Laps 1 (starts mid-observation) and the flushed tail are Incomplete by design; laps 2..3 are the subject.
    private static IEnumerable<TelemetryFrame> Run(LapSim sim) => sim.Run(laps: 4, endPct: 0.1);

    private static DiagRecord[] Records(DiagLog log)
    {
        var buf = new DiagRecord[4096];
        return buf.AsSpan(0, log.DrainRecords(buf)).ToArray();
    }

    [Fact]
    public void Pit_road_marks_pit()
    {
        var sink = Drive(Run(new LapSim().PitRoad(2, 0.9, 1.0)));
        Assert.Equal(LapReasons.Pit, sink.Reasons(2));
        Assert.Equal(LapReasons.None, sink.Reasons(3));
    }

    [Fact]
    public void Incident_increase_marks_incident()
    {
        var sink = Drive(Run(new LapSim().Incident(2, 0.3)));
        Assert.Equal(LapReasons.Incident, sink.Reasons(2));
        Assert.Equal(LapReasons.None, sink.Reasons(3));
    }

    [Fact]
    public void Offtrack_over_half_second_marks_offtrack()
    {
        var sink = Drive(Run(new LapSim().OffTrack(2, 0.4, 0.6)));
        Assert.Equal(LapReasons.OffTrack, sink.Reasons(2));
        Assert.Equal(LapReasons.None, sink.Reasons(3));
        Assert.Equal(0.6, sink.Lap(2).Stats.OffTrackSeconds, 0.05);
    }

    [Fact]
    public void Offtrack_below_limit_is_ignored()
    {
        var sink = Drive(Run(new LapSim().OffTrack(2, 0.4, 0.12)));
        Assert.Equal(LapReasons.None, sink.Reasons(2));
    }

    [Theory]
    [InlineData(0x4000u)]
    [InlineData(0x8000u)]
    [InlineData(0x0008u)]
    public void Caution_flags_mark_caution(uint flags)
    {
        var sink = Drive(Run(new LapSim().Caution(2, 0.2, 0.3, flags)));
        Assert.Equal(LapReasons.Caution, sink.Reasons(2));
        Assert.Equal(LapReasons.None, sink.Reasons(3));
    }

    [Theory]
    [InlineData(0x0004u)]
    [InlineData(0x0001u)]
    public void Other_flags_do_not_mark_caution(uint flags)
    {
        var sink = Drive(Run(new LapSim().Caution(2, 0.2, 0.3, flags)));
        Assert.Equal(LapReasons.None, sink.Reasons(2));
    }

    [Fact]
    public void Refuel_above_threshold_marks_refuel()
    {
        Assert.Equal(LapReasons.Refuel, Drive(Run(new LapSim().Refuel(2, 0.5, 1.0))).Reasons(2));
        Assert.Equal(LapReasons.None, Drive(Run(new LapSim().Refuel(2, 0.5, 0.04))).Reasons(2));
    }

    [Fact]
    public void Refuel_is_measured_against_running_minimum_within_the_lap()
    {
        // Fuel climbs 0.02 L per frame for 5 frames: no single step reaches the limit, the rise from the minimum does.
        var frames = Run(new LapSim()).ToArray();
        int i = Array.FindIndex(frames, f => f.Lap == 2 && f.LapDistPct >= 0.4f);
        for (int k = i; k < frames.Length && frames[k].Lap == 2; k++) frames[k].FuelLevel += 0.02f * Math.Min(k - i + 1, 5);

        var sink = Drive(frames);
        Assert.Equal(LapReasons.Refuel, sink.Reasons(2));
    }

    [Fact]
    public void Tow_marks_reset()
    {
        var sink = Drive(Run(new LapSim().Tow(2, 0.5)));
        Assert.Equal(LapReasons.Reset, sink.Reasons(2));
        Assert.Equal(LapReasons.None, sink.Reasons(3));
    }

    [Fact]
    public void Not_on_track_marks_reset()
    {
        var frames = Run(new LapSim()).ToArray();
        int i = Array.FindIndex(frames, f => f.Lap == 2 && f.LapDistPct >= 0.5f);
        frames[i].IsOnTrack = false;
        var sink = Drive(frames);
        Assert.Equal(LapReasons.Reset, sink.Reasons(2));
        Assert.Equal(LapReasons.None, sink.Reasons(3));
    }

    [Fact]
    public void Impossible_pct_jump_marks_reset()
    {
        var all = Run(new LapSim()).ToArray();
        int i = Array.FindIndex(all, f => f.Lap == 2 && f.LapDistPct >= 0.30f);
        int j = Array.FindIndex(all, f => f.Lap == 2 && f.LapDistPct >= 0.70f);
        double skipped = all[j].SessionTime - all[i + 1].SessionTime + 1.0 / 60;
        var frames = new List<TelemetryFrame>();
        for (int k = 0; k < all.Length; k++)
        {
            if (k > i && k < j) continue;
            var f = all[k];
            if (k >= j) f.SessionTime -= skipped; // keep the time axis continuous: only the position jumps
            frames.Add(f);
        }

        var sink = Drive(frames);
        Assert.Equal(LapReasons.Reset, sink.Reasons(2));
        Assert.Equal(LapReasons.None, sink.Reasons(3));
    }

    [Fact]
    public void Plausible_motion_does_not_mark_reset()
    {
        var sink = Drive(Run(new LapSim(hz: 10)));
        Assert.Equal(LapReasons.None, sink.Reasons(2));
        Assert.Equal(LapReasons.None, sink.Reasons(3));
    }

    [Fact]
    public void Session_time_gap_marks_reset()
    {
        var sink = Drive(Run(new LapSim().TimeGap(2, 0.5, 3.0)));
        Assert.Equal(LapReasons.Reset, sink.Reasons(2));
        Assert.Equal(2.4, sink.Lap(2).FuelUsedL, 0.02);
    }

    [Fact]
    public void Session_time_going_backwards_marks_reset_without_throwing()
    {
        var log = new DiagLog(DiagLevel.Debug);
        var sink = Drive(Run(new LapSim().TimeBackwards(2, 0.5, 10)), log);

        Assert.True(sink.Reasons(2).HasFlag(LapReasons.Reset));
        Assert.All(sink.Laps, l =>
        {
            Assert.True(l.FuelUsedL >= 0, $"fuel lap {l.LapNumber}");
            Assert.True(l.MeasuredTimeS >= 0, $"time lap {l.LapNumber}");
        });
        var gap = Assert.Single(Records(log), r => r.Event == DiagEvent.FrameGap);
        Assert.True(gap.A < 0);
    }

    [Fact]
    public void Time_going_backwards_over_the_line_never_yields_negative_measured_time()
    {
        // The line is crossed while SessionTime jumps back by more than the lap has run so far.
        var sink = Drive(Run(new LapSim().TimeBackwards(2, 0.999, 200)));
        Assert.All(sink.Laps, l =>
        {
            Assert.True(l.FuelUsedL >= 0);
            Assert.True(l.MeasuredTimeS >= 0);
        });
        Assert.True(sink.Reasons(2).HasFlag(LapReasons.Reset));
    }

    [Fact]
    public void Multiple_reasons_accumulate_in_code_order()
    {
        var sink = Drive(Run(new LapSim().OffTrack(2, 0.4, 0.6).Incident(2, 0.3)));
        Assert.Equal(["incident", "offtrack"], LapReasonCodes.ToCodes(sink.Reasons(2)));
    }

    [Fact]
    public void Negative_pct_never_creates_a_boundary_and_marks_reset()
    {
        var baseline = Drive(Run(new LapSim()));
        var frames = Run(new LapSim()).ToArray();
        // car "not in world" for about one second between 0.95 and 0.97
        for (int k = 0; k < frames.Length; k++)
            if (frames[k].Lap == 2 && frames[k].LapDistPct >= 0.95f && frames[k].LapDistPct < 0.9625f)
            {
                frames[k].LapDistPct = -1f;
                frames[k].IsOnTrack = false;
            }

        var sink = Drive(frames);

        Assert.Equal(baseline.Laps.Select(l => l.LapNumber), sink.Laps.Select(l => l.LapNumber));
        Assert.True(sink.Reasons(2).HasFlag(LapReasons.Reset));
        Assert.Equal(LapReasons.None, sink.Reasons(3));
        Assert.All(sink.Laps, l => Assert.True(l.MeasuredTimeS > 10 || l.Stats.EndObserved == false));
        Assert.Equal(2.4, sink.Lap(2).FuelUsedL, 0.02);
    }

    [Fact]
    public void Starting_with_negative_pct_does_not_throw_and_marks_reset()
    {
        var frames = Run(new LapSim()).ToArray();
        frames[0].LapDistPct = -1f;
        var sink = Drive(frames);
        Assert.True(sink.Laps[0].Stats.ResetSeen);
    }

    [Fact]
    public void Emits_LapReasonTriggered_once_per_reason_and_lap()
    {
        var log = new DiagLog(DiagLevel.Debug);
        Drive(Run(new LapSim().OffTrack(2, 0.4, 0.6).Incident(2, 0.3, 2)), log);

        var recs = Records(log).Where(r => r.Event == DiagEvent.LapReasonTriggered).ToArray();
        var off = Assert.Single(recs, r => r.I1 == (int)LapReasons.OffTrack);
        Assert.Equal(2, off.I0);
        Assert.Equal(DiagLevel.Debug, off.Level);
        Assert.InRange(off.A, 0.5, 0.6);
        Assert.InRange(off.B, 0.4, 0.6);
        var inc = Assert.Single(recs, r => r.I1 == (int)LapReasons.Incident);
        Assert.Equal(2, inc.I0);
        Assert.Equal(2, inc.A);
        Assert.InRange(inc.B, 0.3, 0.32);
        Assert.DoesNotContain(recs, r => r.I0 == 3);
    }
}
