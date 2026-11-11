using FuelPace.Core.Strategy;

namespace FuelPace.App.Overlay;

/// <summary>Colors transcribed from the approved mocks.</summary>
public static class OverlayTheme
{
    public static readonly Rgba Label = Rgba.Hex(0xA9B2BD);
    public static readonly Rgba Text = Rgba.Hex(0xF2F4F7);
    public static readonly Rgba Secondary = Rgba.Hex(0xD5DAE0);
    public static readonly Rgba Insight = Rgba.Hex(0xE3E7EC);
    public static readonly Rgba White = Rgba.Hex(0xFFFFFF);
    public static readonly Rgba Accent = Rgba.Hex(0x1E6BD6);
    public static readonly Rgba RecDot = Rgba.Hex(0xFF5A5A);
    public static readonly Rgba Dark = Rgba.Hex(0x0E1013);
    public static readonly Rgba Subtitle = Rgba.Hex(0xC9CFD6);

    public const byte PanelR = 10, PanelG = 12, PanelB = 16;

    public static Rgba Panel(double opacity) => Rgba.From(PanelR, PanelG, PanelB, opacity);

    /// <summary>Per-state colors of the ALVO row and the VOLTA / ÚLT values.</summary>
    public readonly record struct StatePalette(Rgba RowBg, Rgba Ink, Rgba Center, string? BadgeText, Rgba BadgeBg, Rgba BadgeFg, Rgba Volta, Rgba Last);

    public static StatePalette For(SignalState state) => state switch
    {
        SignalState.Blue => new(Rgba.From(47, 128, 237, 0.26), Rgba.Hex(0xCFE2FF), Rgba.Hex(0xE6EEF9), "NO ALVO", Accent, White, Rgba.Hex(0x6AAEFF), Rgba.Hex(0x9CC8FF)),
        SignalState.Red => new(Rgba.From(235, 87, 87, 0.28), Rgba.Hex(0xFFD9D9), Rgba.Hex(0xFBE9E9), "ACIMA", Rgba.Hex(0xC7362F), White, Rgba.Hex(0xFF7A7A), Rgba.Hex(0xFFA8A8)),
        SignalState.NotFeasible => new(Rgba.From(255, 255, 255, 0.12), White, Rgba.Hex(0xE6E9ED), "PIT", Text, Dark, Text, Text),
        _ => new(Rgba.From(255, 255, 255, 0.07), Label, Label, null, default, default, Text, Text),
    };
}
