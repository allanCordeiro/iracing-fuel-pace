using FuelPace.Core.Baselines;
using FuelPace.Core.Display;
using FuelPace.Core.Strategy;
using FuelPace.Tests.Baselines;

namespace FuelPace.Tests.Display;

public class GlanceViewBuilderTests
{
    static readonly GlanceConfig Cfg = GlanceConfig.Default; // res 0.5, S 0.20
    static readonly double N = double.NaN;

    static readonly Baseline FixtureBaseline =
        BaselineCalculator.Compute(BaselineCalculatorTests.Fixture6, 100, [], new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero))!;

    /// <summary>A hand-made engine output with everything unavailable except what the case sets.</summary>
    static OverlayState St(SignalState state, double fuel = double.NaN, double lr = double.NaN, double proj = double.NaN, double last = double.NaN, double mean = double.NaN,
        double target = double.NaN, double lim = double.NaN, double pace = double.NaN, double b = double.NaN) => new()
    {
        State = state, FuelL = fuel, LapsRemaining = lr, LapProjectionL = proj, LastLapL = last, MeanL = mean, TargetL = target,
        FeasibleMinL = lim, PaceS = pace, FuelVsTimeB = b, AutonomyLaps = N, AutonomyMin = N,
        LastLapTimeS = N, MeanLapTimeS = N, BaselineLapTimeS = N,
    };

    static GlanceView Build(in OverlayState s, GlanceConfig? cfg = null) => GlanceViewBuilder.Build(in s, cfg ?? Cfg);

    // ---- V3-1, by hand (state coherent with each row) ----

    [Fact]
    public void V3_1_blue_L19_slack_saturated_left()
    {
        var v = Build(St(SignalState.Blue, fuel: 50, lr: 19, proj: 2.38, last: 2.38, mean: 2.37, target: 49.5 / 19, lim: 2.23));
        Assert.Equal(GlanceMode.Full, v.Mode);
        Assert.Equal(DecisionKind.Ok, v.Decision);
        Assert.Equal(4.3f, v.DecisionValue);
        Assert.Equal(0, v.ComplementPct);
        Assert.True(v.ShowBar);
        Assert.Equal(20, v.BarSteps);
        Assert.Equal(-20, v.BarMarkerQ);
        Assert.Equal(-20, v.BarGhostQ); // ÚLT 2.38 − 2.61 = −23 → saturated at −Q
        Assert.Equal(TargetLabel.Ate, v.TargetLabel);
        Assert.Equal(2.61f, v.Target);
    }

    [Fact]
    public void V3_1_red_L22_save_013_5pct_marker_plus13()
    {
        var v = Build(St(SignalState.Red, fuel: 50, lr: 22, proj: 2.38, last: 2.38, mean: 2.37, target: 49.5 / 22, lim: 2.23));
        Assert.Equal(DecisionKind.Economize, v.Decision);
        Assert.Equal(0.13f, v.DecisionValue);
        Assert.Equal(-5, v.ComplementPct);
        Assert.True(float.IsNaN(v.ComplementSec));
        Assert.Equal(13, v.BarMarkerQ);
        Assert.Equal(TargetLabel.Alvo, v.TargetLabel);
        Assert.Equal(2.25f, v.Target);
    }

    [Fact]
    public void V3_1_not_feasible_L23_no_bar_pit_faltam_1_8()
    {
        var v = Build(St(SignalState.NotFeasible, fuel: 50, lr: 23, proj: 2.38, last: 2.38, mean: 2.37, target: 49.5 / 23, lim: 2.2325));
        Assert.Equal(DecisionKind.Pit, v.Decision);
        Assert.Equal(1.8f, v.DecisionValue);
        Assert.False(v.ShowBar);
        Assert.Equal(GlanceView.BarAbsent, v.BarMarkerQ);
        Assert.Equal(GlanceView.BarAbsent, v.BarGhostQ);
        Assert.Equal(TargetLabel.PitMin, v.TargetLabel);
        Assert.Equal(2.15f, v.Target);
        Assert.Equal(2.23f, v.FeasibleMin);
    }

    // ---- V3-1 / V3-2 through the real engine ----

    static StrategyInput Input(double fuel, double lRest, double lastL, double t) => new()
    {
        SessionTime = t, FuelL = fuel, LapDistPct = 0, LapProjectionL = N, LastLapL = lastL, StintMeanL = N, StintMeanLapS = N,
        LastLapTimeS = N, SessionTimeRemain = N, LeaderPaceS = N, LeaderLapDistPct = N, EstLapTimeS = N,
        IsRaceSession = true, SessionLapsRemainEx = (int)lRest - 1, Race = RaceKind.Laps,
    };

    static OverlayState Engine(in StrategyInput i, ref SignalHysteresis h) =>
        FuelStrategyEngine.Compute(in i, FixtureBaseline, new StrategyConfig(), ref h);

    [Fact]
    public void V3_1_through_the_engine()
    {
        var h = new SignalHysteresis();
        var i = Input(50, 19, 2.38, 100);
        var blue = Engine(in i, ref h);
        Assert.Equal(2.23, Math.Round(blue.FeasibleMinL, 2));
        var vb = Build(blue);
        Assert.Equal((SignalState.Blue, DecisionKind.Ok, 4.3f, 2.61f), (blue.State, vb.Decision, vb.DecisionValue, vb.Target));
        Assert.Equal(-20, vb.BarGhostQ);

        h = new SignalHysteresis();
        i = Input(50, 22, 2.38, 100);
        var red = Engine(in i, ref h);
        var vr = Build(red);
        Assert.Equal((SignalState.Red, DecisionKind.Economize, 0.13f, -5), (red.State, vr.Decision, vr.DecisionValue, (int)vr.ComplementPct));
        Assert.Equal(13, vr.BarGhostQ);
        Assert.Equal(2.25f, vr.Target);

        h = new SignalHysteresis();
        i = Input(50, 23, 2.38, 100);
        var pit = Engine(in i, ref h);
        var vp = Build(pit);
        Assert.Equal((SignalState.NotFeasible, DecisionKind.Pit, 1.8f), (pit.State, vp.Decision, vp.DecisionValue));
        Assert.False(vp.ShowBar);
        Assert.Equal((2.15f, 2.23f), (vp.Target, vp.FeasibleMin));
    }

    [Fact]
    public void V3_2_equality_and_zero_slack_through_the_engine()
    {
        var h = new SignalHysteresis();
        var i = Input(48.1, 20, 2.384, 100);
        var s = Engine(in i, ref h);
        var v = Build(s);
        Assert.Equal(SignalState.Blue, s.State);
        Assert.Equal(DecisionKind.Ok, v.Decision);
        Assert.Equal(0.0f, v.DecisionValue); // max(0, …) keeps it from showing −0.1
        Assert.Equal(2.38f, v.Target);
        Assert.Equal(TargetLabel.Ate, v.TargetLabel);
        Assert.Equal(0, v.BarGhostQ);
    }

    [Fact]
    public void V3_2_hand_made_zero_slack()
    {
        var v = Build(St(SignalState.Blue, fuel: 48.1, lr: 20, last: 2.384, mean: 2.37, target: 47.6 / 20));
        Assert.Equal(0.0f, v.DecisionValue);
        Assert.Equal(2.38f, v.Target);
        Assert.Equal(0, v.BarGhostQ);
    }

    // ---- V3-3 ----

    [Fact]
    public void V3_3_red_mock()
    {
        var v = Build(St(SignalState.Red, fuel: 74, lr: 32, proj: 2.38, last: 2.39, mean: 2.37, target: 73.5 / 32, lim: 2.23));
        Assert.Equal(GlanceMode.Full, v.Mode);
        Assert.Equal(DecisionKind.Economize, v.Decision);
        Assert.Equal(0.08f, v.DecisionValue);
        Assert.Equal(-3, v.ComplementPct);
        Assert.Equal(31, v.LapsPossible);
        Assert.Equal(32, v.LapsRemaining);
        Assert.Equal(74.0f, v.Fuel);
        Assert.Equal(2.30f, v.Target);
        Assert.Equal(8, v.BarMarkerQ);
        Assert.Equal(9, v.BarGhostQ);
        Assert.Equal((2.38f, 2.39f, 2.37f), (v.LapProj, v.LapLast, v.LapAvg));
    }

    [Fact]
    public void V3_3_blue_mock()
    {
        var v = Build(St(SignalState.Blue, fuel: 74, lr: 30, proj: 2.36, last: 2.37, mean: 2.37, target: 73.5 / 30, lim: 2.23));
        Assert.Equal(DecisionKind.Ok, v.Decision);
        Assert.Equal(2.7f, v.DecisionValue);
        Assert.Equal(2.45f, v.Target);
        Assert.Equal(-9, v.BarMarkerQ);
        Assert.Equal(-8, v.BarGhostQ);
        Assert.Equal(30, v.LapsRemaining);
    }

    [Fact]
    public void V3_3_pit_mock()
    {
        var v = Build(St(SignalState.NotFeasible, fuel: 74, lr: 34, proj: 2.38, last: 2.39, mean: 2.37, target: 73.5 / 34, lim: 2.23));
        Assert.Equal(DecisionKind.Pit, v.Decision);
        Assert.Equal(2.3f, v.DecisionValue);
        Assert.Equal(2.16f, v.Target);
        Assert.Equal(2.23f, v.FeasibleMin);
        Assert.False(v.ShowBar);
        Assert.Equal(34, v.LapsRemaining);
    }

    [Fact]
    public void V3_3_no_baseline_is_compact()
    {
        var v = Build(St(SignalState.NoBaseline, fuel: 74, lr: 30, proj: 2.91, last: 2.91, mean: 2.92, pace: 108));
        Assert.Equal(GlanceMode.Compact, v.Mode);
        Assert.Equal(25, v.LapsPossible); // floor(73.5 / 2.92)
        Assert.Equal(45, v.MinutesPossible); // 25 × 108 s = 45 min
        Assert.Equal(DecisionKind.None, v.Decision);
        Assert.True(float.IsNaN(v.DecisionValue));
        Assert.False(v.ShowBar);
        Assert.Equal(TargetLabel.None, v.TargetLabel);
        Assert.True(float.IsNaN(v.Target));
    }

    [Fact]
    public void No_target_is_compact_and_inactive_is_inactive()
    {
        Assert.Equal(GlanceMode.Compact, Build(St(SignalState.NoTarget, fuel: 74, mean: 2.92)).Mode);
        var v = Build(St(SignalState.Inactive, fuel: 74));
        Assert.Equal(GlanceMode.Inactive, v.Mode);
        Assert.Equal(DecisionKind.None, v.Decision);
        Assert.Equal(-1, v.LapsPossible);
        Assert.Equal(-1, v.MinutesPossible);
        Assert.Equal(-1, v.LapsRemaining);
    }

    [Fact]
    public void Inactive_through_the_engine_keeps_laps_and_minutes_possible()
    {
        // PRD v3 §6.5: pit lane / box / garage keep COMB and the autonomy. Stint mean 2.37 L at 80.14 s:
        // floor(73.5 / 2.37) = 31 laps, round(31 × 80.14 / 60) = 41 min.
        var h = new SignalHysteresis();
        var i = Input(74, 20, 2.38, 100);
        i.Inactive = true;
        i.StintMeanL = 2.37;
        i.StintMeanLapS = 80.14;
        var v = Build(Engine(in i, ref h));
        Assert.Equal(GlanceMode.Inactive, v.Mode);
        Assert.Equal((74.0f, (short)31, (short)41), (v.Fuel, v.LapsPossible, v.MinutesPossible));

        // no stint laps yet: the baseline's mean and pace
        i.StintMeanL = N;
        i.StintMeanLapS = N;
        h = new SignalHysteresis();
        v = Build(Engine(in i, ref h));
        short laps = (short)Math.Floor(73.5 / FixtureBaseline.FuelMeanL);
        Assert.Equal(laps, v.LapsPossible);
        Assert.Equal((short)Math.Round(laps * FixtureBaseline.LapMeanS / 60, MidpointRounding.AwayFromZero), v.MinutesPossible);
    }

    [Fact]
    public void Minutes_possible_only_outside_full()
    {
        var s = St(SignalState.Red, fuel: 74, lr: 32, proj: 2.38, last: 2.39, mean: 2.37, target: 73.5 / 32, lim: 2.23, pace: 108);
        Assert.Equal(-1, Build(s).MinutesPossible);
    }

    // ---- V3-4 ----

    [Fact]
    public void V3_4_no_projection_marker_absent_ghost_present()
    {
        var v = Build(St(SignalState.Red, fuel: 74, lr: 32, last: 2.39, mean: 2.37, target: 73.5 / 32, lim: 2.23));
        Assert.Equal(GlanceView.BarAbsent, v.BarMarkerQ);
        Assert.Equal(9, v.BarGhostQ);
    }

    [Fact]
    public void V3_4_no_last_lap_ghost_absent()
    {
        var v = Build(St(SignalState.Red, fuel: 74, lr: 32, proj: 2.38, mean: 2.37, target: 73.5 / 32, lim: 2.23));
        Assert.Equal(8, v.BarMarkerQ);
        Assert.Equal(GlanceView.BarAbsent, v.BarGhostQ);
    }

    [Fact]
    public void V3_4_marker_exactly_on_target_is_step_zero()
    {
        var v = Build(St(SignalState.Blue, fuel: 74, lr: 30, proj: 2.45, last: 2.37, mean: 2.37, target: 73.5 / 30, lim: 2.23));
        Assert.Equal(0, v.BarMarkerQ); // the layout colours q <= 0 blue
    }

    [Fact]
    public void V3_4_half_cent_rounds_up_to_plus_one()
    {
        // VOLTA 2.385 rounds (away from zero) to 2.39; A = 2.38 → +1 (the layout colours q > 0 red, by position).
        var v = Build(St(SignalState.Blue, fuel: 74, lr: 30, proj: 2.385, last: 2.38, mean: 2.37, target: 2.38, lim: 2.23));
        Assert.Equal(1, v.BarMarkerQ);
        Assert.Equal(0, v.BarGhostQ);
    }

    [Fact]
    public void Bar_scale_clamp_and_switch()
    {
        var s = St(SignalState.Red, fuel: 74, lr: 32, proj: 2.38, last: 2.39, mean: 2.37, target: 73.5 / 32, lim: 2.23);
        Assert.Equal(5, Build(s, Cfg with { BarScaleL = 0.01 }).BarSteps);
        Assert.Equal(50, Build(s, Cfg with { BarScaleL = 0.9 }).BarSteps);
        var tight = Build(s, Cfg with { BarScaleL = 0.05 });
        Assert.Equal(5, tight.BarSteps);
        Assert.Equal(5, tight.BarMarkerQ); // +8 saturates at +Q
        var off = Build(s, Cfg with { ShowBar = false });
        Assert.False(off.ShowBar);
        Assert.Equal(GlanceView.BarAbsent, off.BarMarkerQ);
        Assert.Equal(GlanceView.BarAbsent, off.BarGhostQ);
    }

    // ---- V3-5: hysteresis x marker ----

    [Fact]
    public void V3_5_marker_sign_flips_same_tick_decision_after_1_5s()
    {
        var h = new SignalHysteresis();
        var i = Input(74, 32, 2.39, 100);
        i.LapProjectionL = 2.39;
        var first = Engine(in i, ref h);
        Assert.Equal(SignalState.Red, first.State);
        Assert.True(Build(first).BarMarkerQ > 0);

        // Lr 30 and a lower projection: raw is blue, the held state is still red.
        i = Input(74, 30, 2.37, 101);
        i.LapProjectionL = 2.36;
        var held = Engine(in i, ref h);
        var vHeld = Build(held);
        Assert.Equal(SignalState.Red, held.State);
        Assert.Equal(DecisionKind.Economize, vHeld.Decision);
        Assert.True(vHeld.BarMarkerQ < 0); // the marker already follows the position (blue side)
        Assert.Equal(0.0f, vHeld.DecisionValue);   // no "ECONOMIZE 0.00 −1%" during the hold
        Assert.Equal(0, vHeld.ComplementPct);

        i.SessionTime = 102.6;
        var done = Engine(in i, ref h);
        Assert.Equal(SignalState.Blue, done.State);
        Assert.Equal(DecisionKind.Ok, Build(done).Decision);
    }

    // ---- V3-6 ----

    [Fact]
    public void V3_6_caution_estimate()
    {
        var s = St(SignalState.Red, fuel: 74, lr: 32, proj: 2.38, last: 2.39, mean: 2.37, target: 73.5 / 32, lim: 2.23);
        s.TargetIsEstimate = true;
        var v = Build(s);
        Assert.True(v.IsEstimate);
        Assert.True(v.Pills.HasFlag(StatusPills.Caution));
        Assert.False(Build(St(SignalState.Red, fuel: 74, lr: 32, target: 2.3)).Pills.HasFlag(StatusPills.Caution));
    }

    [Fact]
    public void V3_6_through_the_engine_frozen_leader_pace()
    {
        var i = Input(18, 7, 2.38, 100);
        i.Race = RaceKind.Time; i.SessionTimeRemain = 600; i.LeaderLapDistPct = 0.30; i.LeaderPaceS = 100;
        i.LapDistPct = 0.80; i.StintMeanLapS = 102; i.SessionLapsRemainEx = 32767; i.Caution = true;
        var h = new SignalHysteresis();
        var s = Engine(in i, ref h);
        Assert.True(s.TargetIsEstimate);
        var v = Build(s);
        Assert.True(v.IsEstimate);
        Assert.True(v.Pills.HasFlag(StatusPills.Caution));
    }

    // ---- other rules ----

    [Fact]
    public void Complement_in_seconds_uses_fuel_vs_time_slope()
    {
        // −B × saving: B −11.4 s/L × 0.08 L → +0.9 s ("Save" insight of the engine).
        var s = St(SignalState.Red, fuel: 74, lr: 32, proj: 2.38, last: 2.39, mean: 2.37, target: 73.5 / 32, lim: 2.23, b: -11.4);
        var sec = Cfg with { ComplementSeconds = true };
        Assert.Equal(0.9f, Build(s, sec).ComplementSec);
        Assert.True(float.IsNaN(Build(s).ComplementSec)); // option off
        s.FuelVsTimeB = N;
        Assert.True(float.IsNaN(Build(s, sec).ComplementSec)); // no fit → falls back to %
    }

    [Fact]
    public void Complement_pct_is_at_least_one()
    {
        // ref 2.38, A 2.37 → 0.01 → 0.42% → minimum 1
        var v = Build(St(SignalState.Red, fuel: 74, lr: 30, proj: 2.38, last: 2.38, mean: 2.37, target: 2.37, lim: 2.23));
        Assert.Equal(-1, v.ComplementPct);
    }

    [Fact]
    public void No_reference_shows_dash_and_no_complement()
    {
        var v = Build(St(SignalState.Red, fuel: 74, lr: 32, mean: 2.37, target: 73.5 / 32, lim: 2.23, b: -11.4), Cfg with { ComplementSeconds = true });
        Assert.Equal(DecisionKind.Economize, v.Decision);
        Assert.True(float.IsNaN(v.DecisionValue));
        Assert.Equal(0, v.ComplementPct);
        Assert.True(float.IsNaN(v.ComplementSec));
    }

    [Fact]
    public void Laps_clamps_and_noise()
    {
        Assert.Equal(-1, Build(St(SignalState.NoBaseline, fuel: 74, mean: 0)).LapsPossible);        // infinity
        Assert.Equal(0, Build(St(SignalState.NoBaseline, fuel: 0.2, mean: 2.4)).LapsPossible);      // below the reserve: 0, not "—"
        Assert.Equal(0, Build(St(SignalState.NotFeasible, fuel: 0.3, lr: 2, last: 2.38, mean: 2.37, target: N, lim: 2.23)).LapsPossible);
        Assert.Equal(-1, Build(St(SignalState.NoBaseline, fuel: 0.2)).LapsPossible);                // unknown mean stays "—"
        Assert.Equal(-1, Build(St(SignalState.NoBaseline, mean: 2.4)).LapsPossible);                // unknown fuel too
        Assert.Equal(999, Build(St(SignalState.NoBaseline, fuel: 74, mean: 0.01)).LapsPossible);
        Assert.Equal(20, Build(St(SignalState.NoBaseline, fuel: 74, lr: 20.0000000001, mean: 2.4)).LapsRemaining); // fp noise
        Assert.Equal(21, Build(St(SignalState.NoBaseline, fuel: 74, lr: 20.01, mean: 2.4)).LapsRemaining);
        Assert.Equal(999, Build(St(SignalState.NoBaseline, fuel: 74, lr: 5000, mean: 2.4)).LapsRemaining);
    }

    [Fact]
    public void Support_row_pills_and_pace_line_pass_through()
    {
        var s = St(SignalState.Blue, fuel: 74, lr: 30, proj: 2.36, last: 2.37, mean: 2.374, target: 2.45, lim: 2.23);
        s.LastLapInvalid = true; s.MeanFromBaseline = true; s.IsStintMode = true; s.Recording = true; s.BopMismatch = true;
        s.RecValid = 4; s.RecTotal = 9; s.LastLapTimeS = 108.456; s.MeanLapTimeS = N; s.BaselineLapTimeS = 107.1;
        var v = Build(s, Cfg with { ShowAvg = false });
        Assert.True(v.LapLastInvalid);
        Assert.True(v.LapAvgFromBaseline);
        Assert.True(v.IsStint);
        Assert.False(v.ShowAvg);
        Assert.Equal(StatusPills.Rec | StatusPills.BopMismatch, v.Pills);
        Assert.Equal((4, 9), (v.RecValid, v.RecTotal));
        Assert.Equal(2.37f, v.LapAvg);
        Assert.Equal(108.46f, v.LastLapTimeS);
        Assert.True(float.IsNaN(v.MeanLapTimeS));
        Assert.Equal(107.1f, v.BaselineLapTimeS);
    }

    [Fact]
    public void Sub_display_noise_does_not_dirty_the_view()
    {
        var a = St(SignalState.Red, fuel: 74.001, lr: 32, proj: 2.3801, last: 2.39, mean: 2.37, target: 73.5 / 32, lim: 2.23);
        var b = St(SignalState.Red, fuel: 74.002, lr: 32, proj: 2.3802, last: 2.39, mean: 2.37, target: 73.5 / 32, lim: 2.23);
        Assert.Equal(Build(a), Build(b));
    }
}
