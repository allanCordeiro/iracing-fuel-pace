using FuelPace.Core.Baselines;
using FuelPace.Core.Strategy;
using FuelPace.Tests.Baselines;

namespace FuelPace.Tests.Strategy;

public class FuelStrategyEngineTests
{
    static readonly Baseline FixtureBaseline =
        BaselineCalculator.Compute(BaselineCalculatorTests.Fixture6, 100, [], new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero))!;

    static readonly StrategyConfig Default = new();

    static OverlayState Run(StrategyInput input) => Run(input, FixtureBaseline, Default);

    static OverlayState Run(StrategyInput input, Baseline? baseline, StrategyConfig cfg)
    {
        var h = new SignalHysteresis();
        return FuelStrategyEngine.Compute(in input, baseline, cfg, ref h);
    }

    static Insight Rounded(Insight i) => new(i.Kind, Math.Round(i.A, 2), i.B, Math.Round(i.C, 2));

    static class Input
    {
        /// <summary>Everything unavailable (NaN), race session, unlimited.</summary>
        public static StrategyInput Blank() => new()
        {
            SessionTime = 100, FuelL = double.NaN, LapDistPct = 0, LapProjectionL = double.NaN, LastLapL = double.NaN,
            StintMeanL = double.NaN, StintMeanLapS = double.NaN, LastLapTimeS = double.NaN, SessionTimeRemain = double.NaN,
            LeaderPaceS = double.NaN, LeaderLapDistPct = double.NaN, EstLapTimeS = double.NaN,
            IsRaceSession = true, SessionLapsRemainEx = 32767, Race = RaceKind.Unlimited,
        };

        public static StrategyInput LapsRace(double fuelL, double lRest, double refL)
        {
            var i = Blank();
            i.Race = RaceKind.Laps;
            i.FuelL = fuelL;
            i.LapDistPct = 0;
            i.SessionLapsRemainEx = (int)lRest - 1;
            i.LastLapL = refL;
            i.LapProjectionL = double.NaN;
            return i;
        }

        /// <summary>Criterion 9: T_rem 600 s, leader at 0.30 / 100 s, me at 0.80 / 102 s, 18 L.</summary>
        public static StrategyInput TimeRace()
        {
            var i = Blank();
            i.Race = RaceKind.Time;
            i.SessionTimeRemain = 600;
            i.LeaderLapDistPct = 0.30;
            i.LeaderPaceS = 100;
            i.LapDistPct = 0.80;
            i.StintMeanLapS = 102;
            i.FuelL = 18;
            i.LastLapL = 2.38;
            return i;
        }
    }

    // ---- Criterion 3 ----

    [Fact]
    public void L19_is_blue_with_slack()
    {
        var s = Run(Input.LapsRace(50, 19, 2.38));
        Assert.Equal(2.61, Math.Round(s.TargetL, 2)); Assert.Equal(SignalState.Blue, s.State);
        Assert.Equal(InsightKind.Slack, s.Insight.Kind); Assert.Equal(4.28, s.Insight.A, 2);
        Assert.Equal(19, s.LapsRemaining, 9);
    }

    [Fact]
    public void L22_is_red_save_013_5pct()
    {
        var s = Run(Input.LapsRace(50, 22, 2.38));
        Assert.Equal(2.25, Math.Round(s.TargetL, 2)); Assert.Equal(SignalState.Red, s.State);
        Assert.Equal(new Insight(InsightKind.Save, 0.13, 5, double.NaN), Rounded(s.Insight));
    }

    [Fact]
    public void L23_is_not_feasible_missing_18()
    {
        var s = Run(Input.LapsRace(50, 23, 2.38));
        Assert.Equal(SignalState.NotFeasible, s.State); Assert.Equal(2.23, Math.Round(s.FeasibleMinL, 2));
        Assert.Equal(InsightKind.StopRequired, s.Insight.Kind); Assert.Equal(1.8, Math.Round(s.Insight.A, 1));
    }

    // ---- Criterion 4 ----

    [Theory]
    [InlineData(2.38)]
    [InlineData(2.384)]
    public void Equal_after_rounding_is_blue(double refL)
    {
        var s = Run(Input.LapsRace(48.1, 20, refL));
        Assert.Equal(2.38, Math.Round(s.TargetL, 2)); Assert.Equal(SignalState.Blue, s.State);
        Assert.Equal(InsightKind.OnTarget, s.Insight.Kind); Assert.Equal(0.0, s.Insight.A, 2);
    }

    // ---- Criterion 9 (target) ----

    [Fact]
    public void Time_race_target_uses_leader()
    {
        var s = Run(Input.TimeRace());
        Assert.Equal(7.2, s.LapsRemaining, 6);
        Assert.Equal(2.43, Math.Round(s.TargetL, 2));
        Assert.False(s.IsPractice);
    }

    [Fact]
    public void Time_race_as_leader_uses_own_pace()
    {
        var i = Input.TimeRace();
        i.IAmLeader = true;
        Assert.Equal(6.2, Run(i).LapsRemaining, 6);
    }

    [Fact]
    public void Time_practice_uses_own_pace_or_no_target()
    {
        var i = Input.TimeRace();
        i.IsRaceSession = false;
        var s = Run(i);
        Assert.Equal(6.2, s.LapsRemaining, 6);
        Assert.True(s.IsPractice);

        var off = Run(i, FixtureBaseline, new StrategyConfig { PracticeLapsFromTime = false });
        Assert.Equal(SignalState.NoTarget, off.State);
        Assert.True(double.IsNaN(off.TargetL));
    }

    [Fact]
    public void Leader_finished_uses_rest_of_lap()
    {
        var i = Input.TimeRace();
        i.LeaderFinished = true;
        Assert.Equal(0.2, Run(i, FixtureBaseline, new StrategyConfig { ExtraLaps = 1 }).LapsRemaining, 9);
    }

    [Fact]
    public void Own_pace_falls_back_to_baseline_then_estimate()
    {
        var i = Input.TimeRace();
        i.IsRaceSession = false;
        i.StintMeanLapS = double.NaN;
        // baseline LapMeanS 80.1405: ceil(0.8 + 600/80.1405 + eps) - 0.8 = 8 - 0.8 = 7.2, same arrival
        var s = Run(i);
        Assert.Equal(LapsRemainingCalculator.ByTime(600, double.NaN, double.NaN, 0.8, FixtureBaseline.LapMeanS), s.LapsRemaining, 9);

        i.EstLapTimeS = 300;
        var noBase = Run(i, null, Default);
        Assert.Equal(LapsRemainingCalculator.ByTime(600, double.NaN, double.NaN, 0.8, 300), noBase.LapsRemaining, 9);
    }

    // ---- Reference consumption ----

    [Fact]
    public void Projection_takes_precedence_over_last_lap()
    {
        var i = Input.LapsRace(50, 22, 2.38);
        i.LapProjectionL = 2.20;
        var s = Run(i);
        Assert.Equal(SignalState.Blue, s.State);
        Assert.Equal(2.20, s.LapProjectionL, 9);
        Assert.Equal(2.38, s.LastLapL, 9);
    }

    [Fact]
    public void Falls_back_to_mean_when_no_projection_nor_last_lap()
    {
        var i = Input.LapsRace(50, 22, double.NaN);
        var s = Run(i); // MeanL = baseline 2.373 → 2.37 > 2.25
        Assert.Equal(SignalState.Red, s.State);
        Assert.Equal(new Insight(InsightKind.Save, 0.12, 5, double.NaN), Rounded(s.Insight));

        i.StintMeanL = 2.20; // stint mean wins over baseline mean
        Assert.Equal(SignalState.Blue, Run(i).State);
    }

    [Fact]
    public void Regression_adds_seconds_cost()
    {
        var b = FixtureBaseline with { FuelVsTime = new FuelVsTime(200, -50) };
        var s = Run(Input.LapsRace(50, 22, 2.38), b, Default);
        Assert.Equal(InsightKind.Save, s.Insight.Kind);
        Assert.Equal(6.5, s.Insight.C, 1e-9);
    }

    // ---- Insight priority ----

    [Fact]
    public void Bop_mismatch_outranks_save_but_not_stop()
    {
        var red = Input.LapsRace(50, 22, 2.38);
        red.BopMismatch = true;
        var s = Run(red);
        Assert.Equal(SignalState.Red, s.State);
        Assert.Equal(InsightKind.BopMismatch, s.Insight.Kind);
        Assert.True(s.BopMismatch);

        var nf = Input.LapsRace(50, 23, 2.38);
        nf.BopMismatch = true;
        Assert.Equal(InsightKind.StopRequired, Run(nf).Insight.Kind);
    }

    [Fact]
    public void No_baseline_state_and_insight()
    {
        var i = Input.LapsRace(50, 19, 2.38);
        i.StintMeanL = 2.40;
        i.EstLapTimeS = 81;
        var s = Run(i, null, Default);
        Assert.Equal(SignalState.NoBaseline, s.State);
        Assert.Equal(new Insight(InsightKind.NoBaseline), s.Insight);
        Assert.Equal(50, s.FuelL);
        Assert.Equal(2.40, s.MeanL, 9);
        Assert.False(s.MeanFromBaseline);
        Assert.Equal(49.5 / 2.40, s.AutonomyLaps, 9);
        Assert.Equal(49.5 / 2.40 * 81 / 60, s.AutonomyMin, 9);
        Assert.Equal(19, s.LapsRemaining, 9);
        Assert.True(double.IsNaN(s.FeasibleMinL));
        Assert.True(double.IsNaN(s.BaselineLapTimeS));
    }

    // ---- Inactive ----

    [Fact]
    public void Inactive_shows_only_fuel()
    {
        var i = Input.LapsRace(50, 19, 2.38);
        i.Inactive = true;
        i.StintMeanL = 2.40;
        i.LapProjectionL = 2.3;
        var s = Run(i);
        Assert.Equal(SignalState.Inactive, s.State);
        Assert.Equal(new Insight(InsightKind.None), s.Insight);
        Assert.Equal(50, s.FuelL);
        Assert.True(double.IsNaN(s.TargetL));
        Assert.True(double.IsNaN(s.MeanL));
        Assert.True(double.IsNaN(s.LapProjectionL));
        Assert.True(double.IsNaN(s.LastLapL));
        Assert.True(double.IsNaN(s.AutonomyLaps));
        Assert.True(double.IsNaN(s.LapsRemaining));
        Assert.True(double.IsNaN(s.FeasibleMinL));
    }

    [Fact]
    public void Electric_is_inactive_with_unsupported_insight()
    {
        var i = Input.LapsRace(50, 19, 2.38);
        i.IsElectric = true;
        var s = Run(i);
        Assert.Equal(SignalState.Inactive, s.State);
        Assert.Equal(InsightKind.ElectricUnsupported, s.Insight.Kind);
        Assert.True(double.IsNaN(s.TargetL));
    }

    [Fact]
    public void Fuel_below_reserve_is_not_feasible_with_positive_missing()
    {
        var s = Run(Input.LapsRace(0.3, 2, 2.38));
        Assert.Equal(SignalState.NotFeasible, s.State);
        Assert.Equal(InsightKind.StopRequired, s.Insight.Kind);
        Assert.True(s.Insight.A > 0);
        Assert.Equal(2 * FixtureBaseline.FuelMeanL * 0.94 + 0.2, s.Insight.A, 9);
    }

    [Fact]
    public void Zero_or_negative_laps_remaining_is_inactive()
    {
        var cfg = new StrategyConfig { Mode = LapsMode.Stint, StintLaps = 25 };
        var i = Input.LapsRace(50, 19, 2.38);
        i.StintLapsDone = 25;
        i.LapDistPct = 0.5;
        var s = Run(i, FixtureBaseline, cfg);
        Assert.Equal(SignalState.Inactive, s.State);
        Assert.Equal(InsightKind.None, s.Insight.Kind);
        Assert.True(double.IsNaN(s.TargetL));

        i.StintLapsDone = 30;
        Assert.Equal(SignalState.Inactive, Run(i, FixtureBaseline, cfg).State);
    }

    [Fact]
    public void Nan_fuel_is_never_blue_or_red()
    {
        var s = Run(Input.LapsRace(double.NaN, 19, 2.38));
        Assert.Equal(SignalState.NoTarget, s.State);
        Assert.True(double.IsNaN(s.TargetL));
        Assert.Equal(InsightKind.None, s.Insight.Kind);
    }

    // ---- No target / modes ----

    [Fact]
    public void Unlimited_auto_shows_no_target()
    {
        var i = Input.Blank();
        i.IsRaceSession = false;
        i.FuelL = 50;
        i.LastLapL = 2.38;
        var s = Run(i);
        Assert.Equal(SignalState.NoTarget, s.State);
        Assert.True(double.IsNaN(s.TargetL));
        Assert.True(double.IsNaN(s.LapsRemaining));
        Assert.Equal(50, s.FuelL);
        Assert.Equal(2.38, s.LastLapL, 9);
        Assert.Equal(FixtureBaseline.FuelMeanL, s.MeanL, 9);
        Assert.Equal(49.5 / FixtureBaseline.FuelMeanL, s.AutonomyLaps, 9);
        Assert.True(s.IsPractice);
        Assert.Equal(InsightKind.None, s.Insight.Kind);

        i.BopMismatch = true;
        Assert.Equal(InsightKind.BopMismatch, Run(i).Insight.Kind);

        var nb = Run(i, null, Default);
        Assert.Equal(SignalState.NoBaseline, nb.State);
        Assert.Equal(InsightKind.NoBaseline, nb.Insight.Kind);
        Assert.True(double.IsNaN(nb.TargetL));
    }

    [Fact]
    public void Stint_mode_sets_flag_and_laps()
    {
        var i = Input.LapsRace(50, 19, 2.38);
        i.StintLapsDone = 5;
        i.LapDistPct = 0.5;
        var s = Run(i, FixtureBaseline, new StrategyConfig { Mode = LapsMode.Stint, StintLaps = 25, ExtraLaps = 2 });
        Assert.Equal(19.5, s.LapsRemaining, 9);
        Assert.True(s.IsStintMode);
        Assert.Equal(49.5 / 19.5, s.TargetL, 9);
        Assert.False(Run(i).IsStintMode);
    }

    [Fact]
    public void Extra_laps_are_added_in_auto()
    {
        var cfg = new StrategyConfig { ExtraLaps = 1 };
        var s = Run(Input.LapsRace(50, 19, 2.38), FixtureBaseline, cfg);
        Assert.Equal(20, s.LapsRemaining, 9);
        Assert.Equal(49.5 / 20, s.TargetL, 9);

        Assert.Equal(8.2, Run(Input.TimeRace(), FixtureBaseline, cfg).LapsRemaining, 6);
    }

    [Fact]
    public void Caution_in_time_race_marks_estimate()
    {
        var t = Input.TimeRace();
        Assert.False(Run(t).TargetIsEstimate);
        t.Caution = true;
        Assert.True(Run(t).TargetIsEstimate);

        var l = Input.LapsRace(50, 19, 2.38);
        l.Caution = true;
        Assert.False(Run(l).TargetIsEstimate);
    }

    [Fact]
    public void Caution_in_stint_mode_or_after_checkered_is_not_estimate()
    {
        var t = Input.TimeRace();
        t.Caution = true;
        var stint = Run(t, FixtureBaseline, new StrategyConfig { Mode = LapsMode.Stint, StintLaps = 25 });
        Assert.False(double.IsNaN(stint.TargetL));
        Assert.False(stint.TargetIsEstimate);

        t.LeaderFinished = true;
        var checkered = Run(t);
        Assert.False(double.IsNaN(checkered.TargetL));
        Assert.False(checkered.TargetIsEstimate);
    }

    [Fact]
    public void Mean_falls_back_to_baseline_italic()
    {
        var i = Input.LapsRace(50, 19, 2.38);
        var s = Run(i);
        Assert.Equal(2.373, s.MeanL, 3);
        Assert.True(s.MeanFromBaseline);

        i.StintMeanL = 2.40;
        var t = Run(i);
        Assert.Equal(2.40, t.MeanL, 9);
        Assert.False(t.MeanFromBaseline);
    }

    [Fact]
    public void Autonomy_uses_mean_and_own_pace()
    {
        var i = Input.LapsRace(50, 19, 2.38);
        i.StintMeanL = 2.50;
        i.StintMeanLapS = 90;
        var s = Run(i);
        Assert.Equal(19.8, s.AutonomyLaps, 9);
        Assert.Equal(19.8 * 90 / 60, s.AutonomyMin, 9);

        i.StintMeanLapS = double.NaN;
        Assert.Equal(19.8 * FixtureBaseline.LapMeanS / 60, Run(i).AutonomyMin, 9);
    }

    [Fact]
    public void Passes_through_display_fields()
    {
        var i = Input.LapsRace(50, 19, 2.38);
        i.LastLapInvalid = true; i.LastLapTimeS = 79.51; i.StintMeanLapS = 80.14;
        i.Recording = true; i.RecValid = 4; i.RecTotal = 5;
        var s = Run(i);
        Assert.True(s.LastLapInvalid);
        Assert.Equal(79.51, s.LastLapTimeS, 9);
        Assert.Equal(80.14, s.MeanLapTimeS, 9);
        Assert.Equal(FixtureBaseline.LapMedianS, s.BaselineLapTimeS, 9);
        Assert.True(s.Recording); Assert.Equal(4, s.RecValid); Assert.Equal(5, s.RecTotal);
    }

    // ---- Config ----

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    public void Mean_window_out_of_range_throws(int n) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyConfig { MeanWindow = n });

    [Fact]
    public void Config_defaults()
    {
        var c = new StrategyConfig { MeanWindow = 16 };
        Assert.Equal(16, c.MeanWindow);
        Assert.Equal(5, Default.MeanWindow);
        Assert.Equal(0.5, Default.ReserveL); Assert.Equal(0.06, Default.MaxSavingFraction); Assert.Equal(0, Default.ExtraLaps);
        Assert.Equal(LapsMode.Auto, Default.Mode); Assert.Equal(25, Default.StintLaps); Assert.True(Default.PracticeLapsFromTime);
        Assert.Equal(1.5, SignalHysteresis.HoldSeconds);
    }

    // ---- Performance ----

    [Fact]
    public void Compute_allocates_nothing()
    {
        var inputs = new[] { Input.LapsRace(50, 19, 2.38), Input.LapsRace(50, 22, 2.38), Input.LapsRace(50, 23, 2.38), Input.TimeRace(), Input.Blank() };
        var regression = FixtureBaseline with { FuelVsTime = new FuelVsTime(200, -50) };
        var h = new SignalHysteresis();
        double sink = 0;
        for (int k = 0; k < inputs.Length; k++)
            sink += FuelStrategyEngine.Compute(in inputs[k], regression, Default, ref h).FuelL;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 10_000; n++)
        {
            ref readonly var input = ref inputs[n % inputs.Length];
            sink += FuelStrategyEngine.Compute(in input, (n & 1) == 0 ? regression : null, Default, ref h).Insight.A;
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, delta);
        Assert.True(sink != 0 || double.IsNaN(sink));
    }

    [Fact]
    public void Held_color_with_no_baseline_never_throws_or_emits_nan_amount()
    {
        var i = Input.LapsRace(60, 20, 2.38);
        var h = new SignalHysteresis();
        FuelStrategyEngine.Compute(in i, FixtureBaseline, Default, ref h);
        i.SessionTime += 0.1;
        var s = FuelStrategyEngine.Compute(in i, null, Default, ref h);
        Assert.Equal(SignalState.NoBaseline, s.State);
        Assert.Equal(InsightKind.NoBaseline, s.Insight.Kind);
    }
}
