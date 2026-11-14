using FuelPace.Core.Strategy;

namespace FuelPace.Core.Display;

/// <summary>
/// PRD v3 §5: derives the <see cref="GlanceView"/> from the engine's <see cref="OverlayState"/> (whose state already carries
/// the 1.5 s hysteresis) and the display settings. Pure and allocation-free; it never changes a v2.1 formula, it only
/// derives display values on top of the engine's outputs.
/// </summary>
public static class GlanceViewBuilder
{
    const int CountCap = 999;

    public static GlanceView Build(in OverlayState s, in GlanceConfig cfg)
    {
        var state = s.State;
        var mode = state switch
        {
            SignalState.Inactive => GlanceMode.Inactive,
            SignalState.NoBaseline or SignalState.NoTarget => GlanceMode.Compact,
            _ => GlanceMode.Full,
        };
        bool full = mode == GlanceMode.Full;

        double res = cfg.ReserveL, c = s.FuelL, lr = s.LapsRemaining, lim = s.FeasibleMinL;
        double a = R2(s.TargetL);
        // The same reference the engine uses for the colour (v2.1 §7.4): projection, else last lap, else mean; rounded as shown.
        double refL = R2(First(s.LapProjectionL, s.LastLapL, s.MeanL));

        var v = new GlanceView
        {
            Mode = mode,
            Decision = DecisionKind.None,
            DecisionValue = float.NaN,
            ComplementSec = float.NaN,
            BarMarkerQ = GlanceView.BarAbsent,
            BarGhostQ = GlanceView.BarAbsent,
            BarSteps = (byte)Math.Clamp(RoundToInt(cfg.BarScaleL / 0.01), 5, 50),
            LapProj = F(R2(s.LapProjectionL)), LapLast = F(R2(s.LastLapL)), LapAvg = F(R2(s.MeanL)),
            LapLastInvalid = s.LastLapInvalid, LapAvgFromBaseline = s.MeanFromBaseline, ShowAvg = cfg.ShowAvg,
            Fuel = F(R1(c)),
            LapsPossible = -1, LapsRemaining = Cap(Ceil(lr)),
            IsStint = s.IsStintMode, IsEstimate = s.TargetIsEstimate,
            TargetLabel = TargetLabel.None, Target = float.NaN, FeasibleMin = float.NaN,
            MinutesPossible = -1,
            RecValid = (short)s.RecValid, RecTotal = (short)s.RecTotal,
            LastLapTimeS = F(R2(s.LastLapTimeS)), MeanLapTimeS = F(R2(s.MeanLapTimeS)), BaselineLapTimeS = F(R2(s.BaselineLapTimeS)),
        };

        // Below the reserve with a known mean the fuel gives 0 laps, which says more than "—"; an unknown mean or fuel stays -1.
        double possible = Math.Floor((c - res) / s.MeanL);
        if (s.MeanL > 0 && !double.IsNaN(possible) && !double.IsInfinity(possible))
            v.LapsPossible = (short)Math.Clamp(possible, 0, CountCap);

        if (s.Recording) v.Pills |= StatusPills.Rec;
        if (s.BopMismatch) v.Pills |= StatusPills.BopMismatch;
        if (s.TargetIsEstimate) v.Pills |= StatusPills.Caution;

        if (!full)
        {
            // Minutes only where the compact layout shows them: in Full they would only cause redraws.
            if (v.LapsPossible >= 0 && s.PaceS > 0 && !double.IsInfinity(s.PaceS))
                v.MinutesPossible = Cap(Math.Round(v.LapsPossible * s.PaceS / 60, MidpointRounding.AwayFromZero));
            return v;
        }

        // F1
        bool noRef = double.IsNaN(s.LapProjectionL) && double.IsNaN(s.LastLapL);
        switch (state)
        {
            case SignalState.Red:
                v.Decision = DecisionKind.Economize;
                if (!noRef)
                {
                    double save = R2(NonNegative(refL - a));
                    v.DecisionValue = F(save);
                    // During the 1.5 s hold after Red→Blue the state is still Red but ref <= A: no saving, no complement.
                    if (save > 0)
                    {
                        int pct = refL > 0 ? RoundToInt(100 * save / refL) : 0;
                        v.ComplementPct = (sbyte)-Math.Min(Math.Max(1, pct), 127);
                    }
                    if (save > 0 && cfg.ComplementSeconds && !double.IsNaN(s.FuelVsTimeB))
                    {
                        // |s| < 0.05 would show "+0.0 s", which says nothing: the % complement stays instead
                        double sec = R1(-s.FuelVsTimeB * save);
                        if (sec != 0) v.ComplementSec = F(sec);
                    }
                }
                v.TargetLabel = TargetLabel.Alvo;
                break;
            case SignalState.Blue:
                v.Decision = DecisionKind.Ok;
                if (!noRef) v.DecisionValue = F(R1(NonNegative((c - res) - lr * refL)));
                v.TargetLabel = TargetLabel.Ate;
                break;
            default: // NotFeasible
                v.Decision = DecisionKind.Pit;
                if (!noRef) v.DecisionValue = F(R1(NonNegative(lr * lim - (c - res))));
                v.TargetLabel = TargetLabel.PitMin;
                v.FeasibleMin = F(R2(lim));
                break;
        }
        v.Target = F(a);

        // F2: whole hundredths, so a half-cent never lands on the wrong side through floating point.
        v.ShowBar = cfg.ShowBar && state is SignalState.Blue or SignalState.Red;
        if (v.ShowBar && !double.IsNaN(a))
        {
            int q = v.BarSteps;
            v.BarMarkerQ = Step(R2(s.LapProjectionL), a, q);
            v.BarGhostQ = Step(R2(s.LastLapL), a, q);
        }
        return v;
    }

    static sbyte Step(double value, double center, int q)
    {
        if (double.IsNaN(value)) return GlanceView.BarAbsent;
        int d = RoundToInt(value * 100) - RoundToInt(center * 100);
        return (sbyte)Math.Clamp(d, -q, q);
    }

    static double R1(double x) => Round(x, 1);

    static double R2(double x) => Round(x, 2);

    /// <summary>
    /// Half away from zero on the decimal the user would read: the double 2.385 is really 2.38499999…, and Math.Round on it
    /// gives 2.38, but PRD V3-4 wants 2.39. decimal keeps 15 significant digits, so a typed half-cent stays a tie. NaN and
    /// huge values (outside decimal's range) take the plain path; decimal arithmetic does not allocate.
    /// </summary>
    static double Round(double x, int digits) =>
        Math.Abs(x) < 1e9 ? (double)Math.Round((decimal)x, digits, MidpointRounding.AwayFromZero) : Math.Round(x, digits, MidpointRounding.AwayFromZero);

    static int RoundToInt(double x) => (int)Math.Round(x, MidpointRounding.AwayFromZero);

    /// <summary>NaN passes through as NaN (the "—" convention); infinities cannot be shown either.</summary>
    static float F(double x) => double.IsInfinity(x) ? float.NaN : (float)x;

    static double NonNegative(double x) => x > 0 ? x : 0;

    /// <summary>ceil of L_rest, absorbing floating-point noise (20.0000000001 laps is 20, not 21); -1 for NaN.</summary>
    static double Ceil(double lr) => double.IsNaN(lr) || double.IsInfinity(lr) ? -1 : Math.Ceiling(Math.Round(lr, 6));

    static short Cap(double x) => double.IsNaN(x) || x < 0 ? (short)-1 : (short)Math.Min(x, CountCap);

    static double First(double a, double b, double c) => !double.IsNaN(a) ? a : !double.IsNaN(b) ? b : c;
}
