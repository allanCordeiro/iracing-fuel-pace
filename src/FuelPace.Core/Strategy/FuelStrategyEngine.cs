using FuelPace.Core.Baselines;

namespace FuelPace.Core.Strategy;

/// <summary>
/// §7.2–7.5: laps remaining, target, signal state and the prioritized insight. Pure and allocation-free; it logs nothing
/// (the pipeline compares outputs and emits the diagnostics events).
/// </summary>
public static class FuelStrategyEngine
{
    public static OverlayState Compute(in StrategyInput input, Baseline? baseline, StrategyConfig cfg, ref SignalHysteresis hysteresis)
    {
        var s = Blank(in input);

        if (input.IsElectric)
            return Inactive(ref s, new Insight(InsightKind.ElectricUnsupported), input.SessionTime, ref hysteresis);

        s.FuelL = input.FuelL;
        double myPace = Positive(input.StintMeanLapS, Positive(baseline?.LapMeanS ?? double.NaN, input.EstLapTimeS));
        // PRD v3 §6.5: the inactive panel keeps COMB and the autonomy, so the mean and the pace are exposed before the
        // early returns too (the same expressions, assigned earlier; nothing else of the inactive output changes).
        s.MeanFromBaseline = double.IsNaN(input.StintMeanL) && baseline is not null;
        s.MeanL = s.MeanFromBaseline ? baseline!.FuelMeanL : input.StintMeanL;
        s.PaceS = myPace;
        if (input.Inactive)
            return Inactive(ref s, new Insight(InsightKind.None), input.SessionTime, ref hysteresis);

        double lRest = LapsRemaining(in input, cfg, myPace);
        if (lRest <= 0)
            return Inactive(ref s, new Insight(InsightKind.None), input.SessionTime, ref hysteresis);

        double usable = input.FuelL - cfg.ReserveL;
        s.LapProjectionL = input.LapProjectionL;
        s.LastLapL = input.LastLapL;
        s.LastLapInvalid = input.LastLapInvalid;
        s.LastLapTimeS = input.LastLapTimeS;
        s.MeanLapTimeS = input.StintMeanLapS;
        s.BaselineLapTimeS = baseline?.LapMedianS ?? double.NaN;
        s.AutonomyLaps = usable / s.MeanL;
        s.AutonomyMin = s.AutonomyLaps * myPace / 60;
        s.FuelVsTimeB = baseline?.FuelVsTime?.B ?? double.NaN;
        s.LapsRemaining = lRest;
        s.IsStintMode = cfg.Mode == LapsMode.Stint;
        s.IsPractice = !input.IsRaceSession;
        s.BopMismatch = input.BopMismatch;

        double target = usable / lRest;
        double refL = First(input.LapProjectionL, input.LastLapL, s.MeanL);
        double feasible = double.NaN;
        SignalState raw;
        if (baseline is null)
            raw = SignalState.NoBaseline;
        else
        {
            feasible = Math.Min(baseline.FuelMeanL * (1 - cfg.MaxSavingFraction), baseline.FuelMinL);
            s.FeasibleMinL = feasible;
            if (double.IsNaN(target) || double.IsNaN(refL))
                raw = SignalState.NoTarget;
            else if (Round2(target) < Round2(feasible))
                raw = SignalState.NotFeasible;
            else
                raw = Round2(refL) <= Round2(target) ? SignalState.Blue : SignalState.Red;
        }

        if (raw != SignalState.NoTarget && !double.IsNaN(target))
        {
            s.TargetL = target;
            // Ruling R10: "~" means the target rests on the frozen leader pace, i.e. the auto timed-race projection only.
            s.TargetIsEstimate = input.Caution && input.Race == RaceKind.Time && cfg.Mode == LapsMode.Auto && !input.LeaderFinished;
        }

        var displayed = hysteresis.Apply(raw, input.SessionTime);
        s.State = displayed;
        s.Insight = displayed switch
        {
            SignalState.NotFeasible => new Insight(InsightKind.StopRequired, NonNegative(lRest * feasible - usable)),
            SignalState.NoBaseline => new Insight(InsightKind.NoBaseline),
            _ when input.BopMismatch => new Insight(InsightKind.BopMismatch),
            SignalState.Red => Save(refL, target, baseline?.FuelVsTime),
            SignalState.Blue when baseline is not null => new Insight(
                target >= baseline.FuelMeanL + baseline.FuelStdL ? InsightKind.Slack : InsightKind.OnTarget,
                NonNegative(usable - lRest * Round2(refL))),
            _ => new Insight(InsightKind.None),
        };
        return s;
    }

    /// <summary>§7.3 laps remaining for the configured mode and session kind; NaN when there is no limit.</summary>
    static double LapsRemaining(in StrategyInput i, StrategyConfig cfg, double myPace)
    {
        if (cfg.Mode == LapsMode.Stint)
            return LapsRemainingCalculator.Stint(cfg.StintLaps, i.StintLapsDone, i.LapDistPct);

        switch (i.Race)
        {
            case RaceKind.Laps:
                return LapsRemainingCalculator.ByLaps(i.SessionLapsRemainEx, i.LapDistPct) + cfg.ExtraLaps;
            case RaceKind.Time:
                if (!i.IsRaceSession && !cfg.PracticeLapsFromTime) return double.NaN;
                if (i.LeaderFinished) return LapsRemainingCalculator.AfterCheckered(i.LapDistPct);
                // Decision 10: the leader's pace only counts in a race, and not when I am the leader.
                bool useLeader = i.IsRaceSession && !i.IAmLeader;
                return LapsRemainingCalculator.ByTime(i.SessionTimeRemain,
                    useLeader ? i.LeaderLapDistPct : double.NaN, useLeader ? i.LeaderPaceS : double.NaN,
                    i.LapDistPct, myPace) + cfg.ExtraLaps;
            default:
                return double.NaN;
        }
    }

    /// <summary>Decision 6: saving per lap on the rounded values, its whole percent of the reference, and −b × saving seconds.</summary>
    static Insight Save(double refL, double target, FuelVsTime? fit)
    {
        double refR = Round2(refL);
        double save = NonNegative(refR - Round2(target));
        double pct = refR > 0 ? Math.Round(100 * save / refR, MidpointRounding.AwayFromZero) : 0;
        return new Insight(InsightKind.Save, save, pct, fit is null ? double.NaN : -fit.B * save);
    }

    static OverlayState Inactive(ref OverlayState s, Insight insight, double now, ref SignalHysteresis hysteresis)
    {
        s.State = hysteresis.Apply(SignalState.Inactive, now);
        s.Insight = insight;
        return s;
    }

    /// <summary>All values unavailable; the REC pill is independent of the strategy, so its fields always pass through.</summary>
    static OverlayState Blank(in StrategyInput input) => new()
    {
        FuelL = double.NaN, AutonomyLaps = double.NaN, AutonomyMin = double.NaN, LapProjectionL = double.NaN,
        LastLapL = double.NaN, MeanL = double.NaN, TargetL = double.NaN, LapsRemaining = double.NaN,
        FeasibleMinL = double.NaN, LastLapTimeS = double.NaN, MeanLapTimeS = double.NaN, BaselineLapTimeS = double.NaN,
        PaceS = double.NaN, FuelVsTimeB = double.NaN,
        Recording = input.Recording, RecValid = input.RecValid, RecTotal = input.RecTotal,
        Insight = new Insight(InsightKind.None),
    };

    /// <summary>Clamp at 0; NaN becomes 0 (Math.Max(0, NaN) is NaN).</summary>
    static double NonNegative(double x) => x > 0 ? x : 0;

    static double Round2(double x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    static double Positive(double x, double fallback) => x > 0 ? x : fallback;

    static double First(double a, double b, double c) => !double.IsNaN(a) ? a : !double.IsNaN(b) ? b : c;
}
