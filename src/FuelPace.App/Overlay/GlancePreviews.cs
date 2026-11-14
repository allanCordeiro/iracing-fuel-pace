using System.Text;
using FuelPace.Core.Display;

namespace FuelPace.App.Overlay;

/// <summary>One v3 review preview: a fixed view and options.</summary>
public sealed record GlancePreview(string Name, GlanceView View, OverlayViewOptions Options);

/// <summary>
/// The v3 review previews (PRD v3 V3-10, §11.4): the four mocks (screens 9–12) with the V3-3 numbers, plus the edge cases
/// (inactive, saturated bar with every pill, bar off, edit mode, 250 %). tools/FuelPace.Preview writes them as SVG into
/// <c>docs/v3/preview</c>; the views are built by hand so the previews do not depend on the builder.
/// </summary>
public static class GlancePreviews
{
    /// <summary>Padding around the overlay in the SVG, over the game-scene backdrop.</summary>
    public const int Padding = 40;

    public static IReadOnlyList<GlancePreview> All { get; } = Build();

    static List<GlancePreview> Build()
    {
        // V3-3: res 0.5, MÉD 2.37, lim 2.23, C 74.0
        var red = new GlanceView
        {
            Mode = GlanceMode.Full, Decision = DecisionKind.Economize, DecisionValue = 0.08f, ComplementPct = -3, ComplementSec = float.NaN,
            ShowBar = true, BarSteps = 20, BarMarkerQ = 8, BarGhostQ = 9,
            LapProj = 2.38f, LapLast = 2.39f, LapAvg = 2.37f, ShowAvg = true,
            Fuel = 74.0f, LapsPossible = 31, LapsRemaining = 32, TargetLabel = TargetLabel.Alvo, Target = 2.30f, FeasibleMin = float.NaN,
            MinutesPossible = 41, LastLapTimeS = 79.51f, MeanLapTimeS = 80.14f, BaselineLapTimeS = 80.12f,
        };
        var blue = red with
        {
            Decision = DecisionKind.Ok, DecisionValue = 2.7f, ComplementPct = 0, BarMarkerQ = -9, BarGhostQ = -8,
            LapProj = 2.36f, LapLast = 2.37f, LapsRemaining = 30, TargetLabel = TargetLabel.Ate, Target = 2.45f,
        };
        var pit = red with
        {
            Decision = DecisionKind.Pit, DecisionValue = 2.3f, ComplementPct = 0, ShowBar = false,
            BarMarkerQ = GlanceView.BarAbsent, BarGhostQ = GlanceView.BarAbsent,
            LapsRemaining = 34, TargetLabel = TargetLabel.PitMin, Target = 2.16f, FeasibleMin = 2.23f,
        };
        // no baseline (screen 12): MÉD 2.92 → dá 25 v
        var compact = new GlanceView
        {
            Mode = GlanceMode.Compact, Decision = DecisionKind.None, DecisionValue = float.NaN, ComplementSec = float.NaN,
            BarMarkerQ = GlanceView.BarAbsent, BarGhostQ = GlanceView.BarAbsent,
            LapProj = 2.91f, LapLast = 2.91f, LapAvg = 2.92f, ShowAvg = true,
            Fuel = 74.0f, LapsPossible = 25, LapsRemaining = -1, Target = float.NaN, FeasibleMin = float.NaN, MinutesPossible = 45,
            Pills = StatusPills.Rec, RecValid = 4, RecTotal = 9,
            LastLapTimeS = float.NaN, MeanLapTimeS = float.NaN, BaselineLapTimeS = float.NaN,
        };
        var inactive = compact with { Mode = GlanceMode.Inactive, Pills = StatusPills.None };
        // VOLTA 2.50 against ALVO 2.30: +0.20 = the end of the ±0.20 scale; caution freezes the target (estimate)
        var saturated = red with
        {
            DecisionValue = 0.20f, ComplementPct = -8, BarMarkerQ = 20, BarGhostQ = 14, LapProj = 2.50f, LapLast = 2.44f,
            IsEstimate = true, Pills = StatusPills.Rec | StatusPills.BopMismatch | StatusPills.Caution, RecValid = 4, RecTotal = 9,
        };
        var noBar = red with { ShowBar = false };
        var edit = blue with { Pills = StatusPills.Rec, RecValid = 4, RecTotal = 9 };

        var normal = Options();
        return
        [
            new("vermelho", red, normal),
            new("azul", blue, normal),
            new("pit", pit, normal),
            new("compacto", compact, normal),
            new("inativo", inactive, normal),
            new("saturado", saturated, normal),
            new("sem-barra", noBar, normal),
            new("edicao", edit, Options(scale: 2, edit: true)),
            new("escala-250", red, Options(scale: 2.5)),
        ];
    }

    static OverlayViewOptions Options(double scale = 1, bool edit = false) =>
        new(scale, 0.6, 1, false, false, edit, false, "Ctrl+Shift+F", 1840, 980);

    /// <summary>The preview as SVG over the game-scene backdrop, with the deterministic test metrics.</summary>
    public static string Svg(GlancePreview p)
    {
        var list = new DrawList();
        var v = p.View;
        GlanceLayout.Build(in v, p.Options, new FixedMetricsMeasurer(), list);
        return SvgRenderer.Render(list, Padding, true);
    }

    /// <summary>Writes <c>{name}.svg</c> for every preview into <paramref name="dir"/> (created if missing); returns the count.</summary>
    public static int WriteAll(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var p in All)
            File.WriteAllText(Path.Combine(dir, p.Name + ".svg"), Svg(p), new UTF8Encoding(false));
        return All.Count;
    }
}
