using FuelPace.Core.Diagnostics;
using FuelPace.Core.Race;
using FuelPace.Core.Telemetry;

namespace FuelPace.Tests.Race;

/// <summary>Builds synthetic race frames at 60 Hz; LastLapTime follows LapCompleted with a 1 s delay.</summary>
internal sealed class RaceFrames
{
    public TelemetryFrame F;
    public readonly LeaderPaceTracker Tracker;
    private readonly List<(int Car, double Due, float Time)> _due = new();

    public RaceFrames(LeaderPaceTracker tracker)
    {
        Tracker = tracker;
        for (int i = 0; i < CarArray<int>.Length; i++)
        {
            F.CarIdxLapCompleted[i] = -1;
            F.CarIdxTrackSurface[i] = -1;
        }
        F.HasCarIdx = true;
    }

    public void AddCar(int car, int position)
    {
        F.CarIdxPosition[car] = position;
        F.CarIdxLapCompleted[car] = 0;
        F.CarIdxTrackSurface[car] = 3;
        F.CarIdxLapDistPct[car] = 0.5f;
    }

    public void Tick()
    {
        F.SessionTime += 1.0 / 60;
        for (int k = _due.Count - 1; k >= 0; k--)
            if (_due[k].Due <= F.SessionTime)
            {
                F.CarIdxLastLapTime[_due[k].Car] = _due[k].Time;
                _due.RemoveAt(k);
            }
        Tracker.Update(in F);
    }

    public void Step(double seconds)
    {
        int n = (int)Math.Round(seconds * 60);
        for (int i = 0; i < n; i++) Tick();
    }

    public void Lap(int car, float time, bool publishTime = true)
    {
        F.CarIdxLapCompleted[car]++;
        if (publishTime) _due.Add((car, F.SessionTime + 1.0, time));
        Step(0.05);
    }

    /// <summary>Completes laps and waits until their times are published.</summary>
    public void Laps(int car, params float[] times)
    {
        foreach (var t in times) { Lap(car, t); Step(1.5); }
    }
}

public class LeaderPaceTrackerTests
{
    private static (RaceFrames R, LeaderPaceTracker T) Make(int window = 5, DiagLog? log = null)
    {
        var t = new LeaderPaceTracker(window, log);
        var r = new RaceFrames(t);
        r.AddCar(5, 1);
        r.Step(0.2);
        return (r, t);
    }

    [Fact]
    public void Mean_of_last_laps_of_leader()
    {
        var (r, t) = Make();
        r.Laps(5, 100f, 101f, 99f);
        Assert.Equal(5, t.Current.LeaderCarIdx);
        Assert.Equal(100.0, t.Current.PaceS, 0.001);
        Assert.Equal(LeaderPaceSource.Mean, t.Current.Source);
        Assert.False(t.Current.Frozen);
        Assert.Equal(0.5, t.Current.LeaderLapDistPct, 0.001);
    }

    [Fact]
    public void Uses_window_of_N()
    {
        var (r, t) = Make(window: 2);
        r.Laps(5, 100f, 110f, 90f);
        Assert.Equal(100.0, t.Current.PaceS, 0.001);
    }

    [Fact]
    public void Single_lap_uses_best_times_102()
    {
        var (r, t) = Make();
        r.Laps(5, 100f);
        Assert.Equal(102.0, t.Current.PaceS, 0.001);
        Assert.Equal(LeaderPaceSource.Best102, t.Current.Source);
    }

    [Fact]
    public void Pit_lap_is_not_buffered()
    {
        var (r, t) = Make();
        r.Laps(5, 100f);
        r.F.CarIdxOnPitRoad[5] = true; r.Step(1);
        r.F.CarIdxOnPitRoad[5] = false; r.Step(1);
        r.Laps(5, 130f, 100.5f);
        Assert.Equal(2, t.ValidLapCount(5));
    }

    [Fact]
    public void Not_in_world_lap_is_not_buffered()
    {
        var (r, t) = Make();
        r.Laps(5, 100f, 100.5f);
        r.F.CarIdxTrackSurface[5] = -1; r.Step(0.5);
        r.F.CarIdxTrackSurface[5] = 3; r.Step(0.5);
        r.Laps(5, 101f);
        Assert.Equal(2, t.ValidLapCount(5));
    }

    [Fact]
    public void Slow_lap_over_107pct_of_median_is_rejected()
    {
        var (r, t) = Make();
        r.Laps(5, 100f, 100.01f, 99.99f);
        Assert.Equal(3, t.ValidLapCount(5));
        r.Laps(5, 120f);
        Assert.Equal(3, t.ValidLapCount(5));
        Assert.Equal(100.0, t.Current.PaceS, 0.001);
    }

    [Fact]
    public void Caution_freezes_pace_and_skips_laps()
    {
        var (r, t) = Make();
        r.Laps(5, 100f, 101f, 99f);
        double before = t.Current.PaceS;
        r.F.SessionFlags = 0x4000;
        r.Step(0.5);
        Assert.True(t.Current.Frozen);
        Assert.Equal(LeaderPaceSource.Held, t.Current.Source);
        Assert.Equal(before, t.Current.PaceS);
        r.Laps(5, 140f);
        Assert.Equal(3, t.ValidLapCount(5));
        Assert.Equal(before, t.Current.PaceS);
        r.F.SessionFlags = 0; r.Step(0.5);
        Assert.False(t.Current.Frozen);
        Assert.Equal(LeaderPaceSource.Mean, t.Current.Source);
    }

    [Fact]
    public void Leader_change_uses_new_leader_buffer_same_tick()
    {
        var (r, t) = Make();
        r.AddCar(7, 2);
        r.Laps(5, 100f, 100.5f, 99.5f);
        r.Laps(7, 105f, 105.5f, 104.5f);
        Assert.Equal(5, t.Current.LeaderCarIdx);
        r.F.CarIdxPosition[5] = 2; r.F.CarIdxPosition[7] = 1;
        r.Tick();
        Assert.Equal(7, t.Current.LeaderCarIdx);
        Assert.Equal(105.0, t.Current.PaceS, 0.001);
        Assert.Equal(LeaderPaceSource.Mean, t.Current.Source);
    }

    [Fact]
    public void Leader_in_pit_holds_last_pace()
    {
        var (r, t) = Make();
        r.Laps(5, 100f, 101f, 99f);
        double before = t.Current.PaceS;
        r.F.CarIdxOnPitRoad[5] = true;
        r.Tick();
        Assert.Equal(LeaderPaceSource.Held, t.Current.Source);
        Assert.Equal(before, t.Current.PaceS);
        Assert.Equal(5, t.Current.LeaderCarIdx);
    }

    [Fact]
    public void No_leader_or_no_data_is_nan()
    {
        var t = new LeaderPaceTracker();
        var r = new RaceFrames(t);
        r.AddCar(3, 2);
        r.Step(0.2);
        Assert.Equal(-1, t.Current.LeaderCarIdx);
        Assert.True(double.IsNaN(t.Current.PaceS));
        Assert.Equal(LeaderPaceSource.None, t.Current.Source);

        r.F.CarIdxPosition[3] = 1; r.Tick();
        Assert.Equal(3, t.Current.LeaderCarIdx);
        Assert.True(double.IsNaN(t.Current.PaceS));
        Assert.Equal(LeaderPaceSource.None, t.Current.Source);
    }

    [Fact]
    public void Missing_LastLapTime_update_drops_lap_after_5s()
    {
        var log = new DiagLog(DiagLevel.Debug);
        var (r, t) = Make(log: log);
        r.Lap(5, 100f, publishTime: false);
        r.Step(4.5);
        Assert.Equal(0, t.ValidLapCount(5));
        Assert.Equal(0, Drain(log).Count(x => x.Event == DiagEvent.LeaderLapRejected));
        r.Step(1.0);
        var rejected = Drain(log).Where(x => x.Event == DiagEvent.LeaderLapRejected).ToList();
        Assert.Single(rejected);
        Assert.Equal(5, rejected[0].I0);
        Assert.Equal(5, rejected[0].I1);
        Assert.Equal(0, t.ValidLapCount(5));
    }

    [Fact]
    public void Emits_leader_events()
    {
        var log = new DiagLog(DiagLevel.Trace);
        var (r, t) = Make(log: log);
        r.Laps(5, 100f, 100.5f);
        r.F.CarIdxOnPitRoad[5] = true; r.Step(0.5);
        r.F.CarIdxOnPitRoad[5] = false; r.Step(0.5);
        r.Laps(5, 130f);
        var recs = Drain(log);
        var changed = recs.Single(x => x.Event == DiagEvent.LeaderChanged);
        Assert.Equal(-1, changed.I0);
        Assert.Equal(5, changed.I1);
        Assert.Equal(2, recs.Count(x => x.Event == DiagEvent.LeaderLapAccepted));
        var rej = recs.Single(x => x.Event == DiagEvent.LeaderLapRejected);
        Assert.Equal(0, rej.I1);
        Assert.Equal(130.0, rej.A, 0.001);
        Assert.Contains(recs, x => x.Event == DiagEvent.LeaderPaceSource && x.I0 == (int)LeaderPaceSource.Mean);
        Assert.Contains(recs, x => x.Event == DiagEvent.LeaderPaceSource && x.I0 == (int)LeaderPaceSource.Best102);
    }

    [Fact]
    public void Window_must_be_2_to_16()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeaderPaceTracker(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeaderPaceTracker(17));
        _ = new LeaderPaceTracker(2);
        _ = new LeaderPaceTracker(16);
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        var log = new DiagLog(DiagLevel.Trace);
        var t = new LeaderPaceTracker(5, log);
        var f = new TelemetryFrame();
        for (int i = 0; i < 64; i++) { f.CarIdxLapCompleted[i] = -1; f.CarIdxTrackSurface[i] = -1; }
        for (int c = 0; c < 20; c++)
        {
            f.CarIdxPosition[c] = c + 1; f.CarIdxLapCompleted[c] = 0; f.CarIdxTrackSurface[c] = 3;
            f.CarIdxLapDistPct[c] = 0.3f;
        }
        void Run(int from, int ticks)
        {
            for (int k = from; k < from + ticks; k++)
            {
                f.SessionTime = k / 60.0;
                for (int c = 0; c < 20; c++)
                {
                    int phase = (k + c * 7) % 600;
                    if (phase == 0) f.CarIdxLapCompleted[c]++;
                    if (phase == 60) f.CarIdxLastLapTime[c] = 90f + (c % 5) + (k % 3) * 0.1f;
                    f.CarIdxOnPitRoad[c] = (k / 600 + c) % 4 == 0 && phase > 300; // some pit laps rejected
                }
                f.SessionFlags = (k / 3000) % 5 == 4 ? 0x4000u : 0;
                t.Update(in f);
            }
        }
        Run(0, 3000); // warm up (JIT)
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(3000, 10_000);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, delta);
        var recs = Drain(log);
        Assert.Contains(recs, x => x.Event == DiagEvent.LeaderLapAccepted);
        Assert.Contains(recs, x => x.Event == DiagEvent.LeaderLapRejected);
    }

    private static List<DiagRecord> Drain(DiagLog log)
    {
        var buf = new DiagRecord[20000];
        int n = log.DrainRecords(buf);
        return buf.Take(n).ToList();
    }
}
