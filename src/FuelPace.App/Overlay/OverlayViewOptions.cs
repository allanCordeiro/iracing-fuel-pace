namespace FuelPace.App.Overlay;

/// <summary>How the overlay is drawn, apart from the data (<see cref="GlanceLayout"/>): the user's appearance settings, the
/// edit mode and the monitor's DPI.</summary>
/// <param name="Scale">The user's scale (0.5–2.5), shown in the edit-mode footer.</param>
/// <param name="ShowPaceLine">The optional pace line (PRD v3 §5.5).</param>
/// <param name="DebugTag">The DBG pill.</param>
/// <param name="ScreenX">Shown in the edit-mode footer, with <paramref name="ScreenY"/>.</param>
/// <param name="DpiScale">The monitor's dpi / 96: the panel is drawn at Scale × DpiScale and the edit chrome at DpiScale,
/// so the list is in physical pixels.</param>
public sealed record OverlayViewOptions(
    double Scale, double BackgroundOpacity, double ContentOpacity, bool ShowPaceLine,
    bool EditMode, bool DebugTag, string ToggleEditHotkey, int ScreenX, int ScreenY, double DpiScale = 1.0);
