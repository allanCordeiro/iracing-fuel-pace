using FuelPace.Core.Display;

namespace FuelPace.App.Overlay;

/// <summary>
/// PRD v3 §4.2 palette (and the v3 mocks). Standalone on purpose: the v2.1 <see cref="OverlayTheme"/> goes away with the
/// v2.1 layout at integration, so the colors the v3 layout shares with it (panel, text, edit chrome) are repeated here.
/// </summary>
public static class GlanceTheme
{
    public static readonly Rgba Text = Rgba.Hex(0xF2F4F7);
    public static readonly Rgba Secondary = Rgba.Hex(0xD5DAE0);
    public static readonly Rgba Label = Rgba.Hex(0xA9B2BD);
    public static readonly Rgba White = Rgba.Hex(0xFFFFFF);
    public static readonly Rgba Dark = Rgba.Hex(0x0E1013);
    public static readonly Rgba RecDot = Rgba.Hex(0xFF5A5A);
    public static readonly Rgba Divider = Rgba.From(169, 178, 189, 0.22);

    /// <summary>Red complement ("−3%", "+0.9 s").</summary>
    public static readonly Rgba Complement = Rgba.Hex(0xFFD9D9);

    // F2 delta bar
    public static readonly Rgba BarBlue = Rgba.From(47, 128, 237, 0.42);
    public static readonly Rgba BarRed = Rgba.From(235, 87, 87, 0.42);
    public static readonly Rgba BarTarget = Rgba.Hex(0xF2F4F7);
    public static readonly Rgba BarGhost = Rgba.From(242, 244, 247, 0.55);
    public static readonly Rgba MarkerBlue = Rgba.Hex(0x6AAEFF);
    public static readonly Rgba MarkerRed = Rgba.Hex(0xFF7A7A);

    // Edit-mode chrome (same as v2.1)
    public static readonly Rgba Accent = Rgba.Hex(0x1E6BD6);
    public static readonly Rgba ChromeText = Rgba.Hex(0xE3E7EC);

    public const byte PanelR = 10, PanelG = 12, PanelB = 16;

    /// <summary>Panel and pill background, rgba(10,12,16, opacity).</summary>
    public static Rgba Panel(double opacity) => Rgba.From(PanelR, PanelG, PanelB, opacity);

    /// <summary>Colors that follow the decision state: the F1 block and selo, the F1 label and unit (Ink), VOLTA and ÚLT.</summary>
    public readonly record struct DecisionPalette(Rgba BlockBg, Rgba SeloBg, Rgba SeloFg, Rgba Ink, Rgba Volta, Rgba Last);

    static readonly DecisionPalette Economize = new(Rgba.From(235, 87, 87, 0.30), Rgba.Hex(0xC7362F), White, Complement, Rgba.Hex(0xFF7A7A), Rgba.Hex(0xFFA8A8));
    static readonly DecisionPalette Ok = new(Rgba.From(47, 128, 237, 0.28), Rgba.Hex(0x1E6BD6), White, Rgba.Hex(0xCFE2FF), Rgba.Hex(0x6AAEFF), Rgba.Hex(0x9CC8FF));
    static readonly DecisionPalette Pit = new(Rgba.From(255, 255, 255, 0.14), Text, Dark, Secondary, Secondary, Secondary);
    static readonly DecisionPalette Neutral = new(Rgba.From(255, 255, 255, 0.07), Secondary, Dark, Secondary, Secondary, Secondary);

    public static DecisionPalette For(DecisionKind d) => d switch
    {
        DecisionKind.Economize => Economize,
        DecisionKind.Ok => Ok,
        DecisionKind.Pit => Pit,
        _ => Neutral, // compact and inactive: support values stay neutral
    };

    /// <summary>The selo word (PRD v3 §5.1); empty when there is no decision.</summary>
    public static string Word(DecisionKind d) => d switch
    {
        DecisionKind.Economize => "ECONOMIZE",
        DecisionKind.Ok => "OK",
        DecisionKind.Pit => "PIT",
        _ => "",
    };

    /// <summary>The F1 label between the selo and the number; empty for ECONOMIZE.</summary>
    public static string DecisionLabel(DecisionKind d) => d switch
    {
        DecisionKind.Ok => "FOLGA",
        DecisionKind.Pit => "FALTAM",
        _ => "",
    };

    /// <summary>The F4 label in front of the per-lap target (PRD v3 §5.4).</summary>
    public static string TargetWord(TargetLabel t) => t switch
    {
        TargetLabel.Alvo => "ALVO",
        TargetLabel.Ate => "ATÉ",
        TargetLabel.PitMin => "MÍN",
        _ => "",
    };
}
