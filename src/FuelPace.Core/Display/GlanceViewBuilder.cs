using FuelPace.Core.Strategy;

namespace FuelPace.Core.Display;

/// <summary>
/// PRD v3 §5: derives the <see cref="GlanceView"/> from the engine's <see cref="OverlayState"/> (whose state already carries
/// the 1.5 s hysteresis) and the display settings. Pure and allocation-free; it never changes a v2.1 formula, it only
/// derives display values on top of the engine's outputs.
/// </summary>
public static class GlanceViewBuilder
{
    public static GlanceView Build(in OverlayState s, in GlanceConfig cfg)
    {
        // Onda 0 skeleton: front A implements §5.
        return new GlanceView
        {
            Mode = GlanceMode.Inactive,
            DecisionValue = float.NaN, ComplementSec = float.NaN,
            BarMarkerQ = GlanceView.BarAbsent, BarGhostQ = GlanceView.BarAbsent,
            LapProj = float.NaN, LapLast = float.NaN, LapAvg = float.NaN, Fuel = float.NaN,
            LapsPossible = -1, LapsRemaining = -1, Target = float.NaN, FeasibleMin = float.NaN, MinutesPossible = -1,
            LastLapTimeS = float.NaN, MeanLapTimeS = float.NaN, BaselineLapTimeS = float.NaN,
        };
    }
}
