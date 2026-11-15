using FuelPace.Core.Strategy;

namespace FuelPace.Tests.Strategy;

/// <summary>
/// Feedback 2026-11-14 (2 h timed race, Motegi): with the finish near a lap boundary, the laps remaining flipped between N and
/// N+1 every few seconds (the target jumped 2.75 ↔ 3.00 L and the colour red ↔ blue). Within a lap LapDistPct runs up to ~0.02 lap
/// ahead of or behind a linear advance (measured on the 2026-11-13 Motegi recording), so pct + T/pace swings by ~0.04 lap and a
/// plain ceil() flips whenever that swing straddles an integer.
/// </summary>
[Collection(FuelPace.Tests.Perf.PerfCollection.Name)]
public class TimedLapsHoldTests
{
    const double Dt = 0.1; // the strategy runs at 10 Hz
    const int Own = TimedLapsHold.OwnPaceKey;

    /// <summary>Fraction of the lap and laps done at <paramref name="tau"/> s for a car with a ±0.02 lap swing around its mean pace.</summary>
    static (double pct, int done) Car(double tau, double paceS, double startPct = 0)
    {
        double laps = startPct + tau / paceS;
        int done = (int)Math.Floor(laps);
        double frac = laps - done;
        return (frac + 0.02 * Math.Sin(2 * Math.PI * frac), done);
    }

    /// <summary>The lap on which I finish, counted from the start of the run: laps done + ceil-part of the result.</summary>
    static int FinishLap(double lapsRemaining, double myPct, int myDone) => myDone + (int)Math.Round(lapsRemaining + myPct);

    [Fact]
    public void Own_pace_swing_across_the_boundary_holds_one_finish_lap()
    {
        const double pace = 108, total = 30.001 * pace; // the clock runs out a hair after the 30th crossing
        var hold = new TimedLapsHold();
        var held = new HashSet<int>();
        var plain = new HashSet<int>();
        for (double tau = 0; tau < 3 * pace; tau += Dt)
        {
            var (pct, done) = Car(tau, pace);
            double t = total - tau;
            held.Add(FinishLap(hold.ByTime(tau, t, Own, double.NaN, double.NaN, pct, pace), pct, done));
            plain.Add(FinishLap(LapsRemainingCalculator.ByTime(t, double.NaN, double.NaN, pct, pace), pct, done));
        }
        Assert.Equal(2, plain.Count); // the scenario reproduces the flip
        Assert.Single(held);
    }

    [Fact]
    public void Leader_swing_across_the_boundary_holds_one_finish_lap()
    {
        const double leaderPace = 100, myPace = 102, total = 25.001 * leaderPace;
        var hold = new TimedLapsHold();
        var held = new HashSet<int>();
        var plain = new HashSet<int>();
        for (double tau = 0; tau < 3 * myPace; tau += Dt)
        {
            var (lp, _) = Car(tau, leaderPace);
            var (mp, done) = Car(tau, myPace); // 2500/102 or 2600/102 s to go: my own stage stays half a lap from its boundary
            double t = total - tau;
            held.Add(FinishLap(hold.ByTime(tau, t, 7, lp, leaderPace, mp, myPace), mp, done));
            plain.Add(FinishLap(LapsRemainingCalculator.ByTime(t, lp, leaderPace, mp, myPace), mp, done));
        }
        Assert.Equal(2, plain.Count);
        Assert.Single(held);
    }

    [Fact]
    public void My_swing_against_the_leader_finish_holds_one_finish_lap()
    {
        // A linear leader far from his boundary; I swing around the moment he takes the flag.
        const double leaderPace = 100, myPace = 101.5, total = 25.5 * leaderPace;
        var hold = new TimedLapsHold();
        var held = new HashSet<int>();
        var plain = new HashSet<int>();
        // my start makes pct + finish/pace an integer + 0.001: I reach the line just as he finishes
        double finish0 = (Math.Ceiling(0.0 + total / leaderPace) - 0.0) * leaderPace;
        double myStart = 1.001 - (finish0 / myPace - Math.Floor(finish0 / myPace));
        for (double tau = 0; tau < 3 * myPace; tau += Dt)
        {
            double lLaps = tau / leaderPace;
            double lp = lLaps - Math.Floor(lLaps);
            var (mp, done) = Car(tau, myPace, myStart);
            double t = total - tau;
            held.Add(FinishLap(hold.ByTime(tau, t, 7, lp, leaderPace, mp, myPace), mp, done));
            plain.Add(FinishLap(LapsRemainingCalculator.ByTime(t, lp, leaderPace, mp, myPace), mp, done));
        }
        Assert.Equal(2, plain.Count);
        Assert.Single(held);
    }

    [Fact]
    public void First_value_is_the_plain_formula()
    {
        var hold = new TimedLapsHold();
        Assert.Equal(LapsRemainingCalculator.ByTime(600, 0.30, 100, 0.80, 102), hold.ByTime(0, 600, 3, 0.30, 100, 0.80, 102), 9);
    }

    [Fact]
    public void A_move_past_the_band_switches()
    {
        const double pace = 108;
        var hold = new TimedLapsHold();
        double t = 30.001 * pace;
        double a = hold.ByTime(0, t, Own, double.NaN, double.NaN, 0.0, pace);
        // 0.1 lap less time (the pace estimate moved): one lap fewer, beyond the 0.05 lap band
        double b = hold.ByTime(Dt, t - 0.1 * pace, Own, double.NaN, double.NaN, 0.0, pace);
        Assert.Equal(a - 1, b, 9);
        Assert.Equal(LapsRemainingCalculator.ByTime(t - 0.1 * pace, double.NaN, double.NaN, 0.0, pace), b, 9);
    }

    [Fact]
    public void A_move_inside_the_band_holds()
    {
        const double pace = 108;
        var hold = new TimedLapsHold();
        double t = 30.001 * pace;
        double a = hold.ByTime(0, t, Own, double.NaN, double.NaN, 0.0, pace);
        double b = hold.ByTime(Dt, t - 0.03 * pace, Own, double.NaN, double.NaN, 0.0, pace);
        Assert.Equal(a, b, 9);
    }

    [Fact]
    public void Crossing_the_line_counts_one_lap_down()
    {
        const double pace = 108;
        var hold = new TimedLapsHold();
        double t = 30.5 * pace;
        double before = hold.ByTime(0, t, Own, double.NaN, double.NaN, 0.999, pace);
        double after = hold.ByTime(0.2, t - 0.2, Own, double.NaN, double.NaN, 0.001, pace);
        Assert.Equal(before - 0.002, after, 6); // 32 - 0.999, then 31 - 0.001
        Assert.Equal(LapsRemainingCalculator.ByTime(t - 0.2, double.NaN, double.NaN, 0.001, pace), after, 9);
    }

    [Fact]
    public void A_gap_a_new_leader_or_the_clock_at_zero_take_the_plain_formula()
    {
        const double pace = 108;
        double t = 30.001 * pace;
        double held(TimedLapsHold h) => h.ByTime(0, t, 7, 0.0, pace, 0.5, pace);

        // the leader is a hair past the boundary at first, then 0.03 lap back: held, unless the history is broken
        var gap = new TimedLapsHold(); held(gap);
        Assert.Equal(LapsRemainingCalculator.ByTime(t - 0.03 * pace, 0.0, pace, 0.5, pace),
            gap.ByTime(5, t - 0.03 * pace, 7, 0.0, pace, 0.5, pace), 9);

        var leader = new TimedLapsHold(); held(leader);
        Assert.Equal(LapsRemainingCalculator.ByTime(t - 0.03 * pace, 0.0, pace, 0.5, pace),
            leader.ByTime(Dt, t - 0.03 * pace, 8, 0.0, pace, 0.5, pace), 9);

        var zero = new TimedLapsHold();
        zero.ByTime(0, 0.04 * pace, 7, 0.97, pace, 0.5, pace); // the leader should still cross before zero: one more lap
        Assert.Equal(LapsRemainingCalculator.ByTime(0, 0.975, pace, 0.5, pace),
            zero.ByTime(Dt, 0, 7, 0.975, pace, 0.5, pace), 9);
    }

    [Fact]
    public void Unavailable_inputs_give_nan()
    {
        var hold = new TimedLapsHold();
        Assert.True(double.IsNaN(hold.ByTime(0, double.NaN, Own, double.NaN, double.NaN, 0.5, 100)));
        Assert.True(double.IsNaN(hold.ByTime(Dt, 600, Own, double.NaN, double.NaN, 0.5, double.NaN)));
        Assert.True(double.IsNaN(hold.ByTime(2 * Dt, 600, Own, double.NaN, double.NaN, double.NaN, 100)));
    }

    [Fact]
    public void Does_not_allocate()
    {
        var hold = new TimedLapsHold();
        double sink = hold.ByTime(0, 600, 3, 0.3, 100, 0.8, 102);
        var window = AllocationMeasure.Start();
        for (int i = 1; i <= 1000; i++) sink += hold.ByTime(i * Dt, 600 - i * Dt, 3, 0.3, 100, 0.8, 102);
        window.AssertNone();
        Assert.True(sink > 0);
    }
}
