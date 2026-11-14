using FuelPace.Core.Display;

namespace FuelPace.App.Overlay;

/// <summary>
/// PRD v3 §4 and §6: lays the v3 overlay out as a <see cref="DrawList"/> from a <see cref="GlanceView"/> (300 px wide at
/// 1x; Scale × DpiScale for the panel, DpiScale only for the edit chrome, as <see cref="OverlayLayout"/>). Lives in App,
/// next to the DrawList and the text measurer, so the geometry is tested on Linux. Zero allocation once warmed up.
/// </summary>
public static class GlanceLayout
{
    /// <summary>Onda 0 skeleton: front B implements it. <see cref="OverlayViewOptions.ShowInsightLine"/> is ignored.</summary>
    public static void Build(in GlanceView v, OverlayViewOptions o, ITextMeasurer m, DrawList dest)
    {
        dest.Clear();
    }

    /// <summary>Where the panel's top-left corner sits inside the list (physical px) in edit mode; same chrome as v2.1.</summary>
    public static SizeF PanelOffset(bool editMode, double dpiScale = 1.0) => OverlayLayout.PanelOffset(editMode, dpiScale);
}
