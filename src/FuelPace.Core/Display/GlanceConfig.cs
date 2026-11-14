namespace FuelPace.Core.Display;

/// <summary>
/// The display settings the v3 builder needs (PRD v3 §7): the reserve of the strategy (for "dá N v" and the folga/faltam
/// numbers), the bar scale S in L/lap (CFG-V3-02, 0.05–0.50), the red complement in seconds instead of percent
/// (CFG-V3-03), the delta bar (CFG-V3-01) and MÉD in the support row (CFG-V3-04).
/// </summary>
public readonly record struct GlanceConfig(double ReserveL, double BarScaleL, bool ComplementSeconds, bool ShowBar, bool ShowAvg)
{
    public const double DefaultBarScaleL = 0.20, MinBarScaleL = 0.05, MaxBarScaleL = 0.50;

    public static GlanceConfig Default => new(0.5, DefaultBarScaleL, false, true, true);
}
