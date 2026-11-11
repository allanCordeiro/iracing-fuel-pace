using FuelPace.App.Overlay;
using FuelPace.Core.Strategy;
using FuelPace.Tests.Support;

namespace FuelPace.App.Tests.Overlay;

[Collection(PerfCollection.Name)]
public sealed class OverlayLayoutTests
{
    static readonly FixedMetricsMeasurer M = new();

    static readonly Rgba White = new(0xF2, 0xF4, 0xF7, 255);

    static OverlayViewOptions Opts(double scale = 1, double bg = 0.6, double content = 1, bool pace = false, bool insight = true,
        bool edit = false, bool debug = false) =>
        new(scale, bg, content, pace, insight, edit, debug, "Ctrl+Shift+F", 1840, 980);

    static OverlayState Blue() => new()
    {
        State = SignalState.Blue, FuelL = 41.8, AutonomyLaps = 17.3, AutonomyMin = 23,
        LapProjectionL = 2.36, LastLapL = 2.39, MeanL = 2.38, TargetL = 2.49, LapsRemaining = 16.6,
        LastLapTimeS = 79.51, MeanLapTimeS = 80.14, BaselineLapTimeS = 80.12,
        Insight = new Insight(InsightKind.Slack, 1.8),
    };

    static DrawList Build(in OverlayState s, OverlayViewOptions? o = null)
    {
        var d = new DrawList();
        OverlayLayout.Build(in s, "Ligier", "Zolder Alt", o ?? Opts(), M, d);
        return d;
    }

    static List<(string Text, DrawCommand Cmd)> Texts(DrawList d)
    {
        var r = new List<(string, DrawCommand)>();
        foreach (var c in d.Commands)
            if (c.Kind == CommandKind.Text) r.Add((new string(d.TextOf(in c)), c));
        return r;
    }

    static DrawCommand Text(DrawList d, string text)
    {
        foreach (var (t, c) in Texts(d)) if (t == text) return c;
        throw new Xunit.Sdk.XunitException($"text '{text}' not found; have: {string.Join(" | ", Texts(d).Select(x => x.Text))}");
    }

    static bool Has(DrawList d, string text) => Texts(d).Any(t => t.Text == text);

    [Fact]
    public void Blue_state_has_blue_row_badge_and_volta_color()
    {
        var d = Build(Blue());
        var row = d.Commands.ToArray().Single(c => c.Kind == CommandKind.FillRoundedRect && c.Color == new Rgba(47, 128, 237, 66));
        Assert.Equal(7f, row.Rect.X, 0.01);
        Assert.Equal(286f, row.Rect.W, 0.01);
        Assert.Contains(d.Commands.ToArray(), c => c.Kind == CommandKind.FillRoundedRect && c.Color == new Rgba(0x1E, 0x6B, 0xD6, 255));
        Assert.True(Has(d, "NO ALVO"));
        Assert.Equal(new Rgba(0x6A, 0xAE, 0xFF, 255), Text(d, "2.36").Color);
        Assert.Equal(FontRole.ValueBold, Text(d, "2.36").Role);
        Assert.Equal(new Rgba(0x9C, 0xC8, 0xFF, 255), Text(d, "2.39").Color);
        Assert.Equal(White, Text(d, "2.38").Color);
        Assert.True(Has(d, "faltam 16.6 v"));
        Assert.True(Has(d, "17.3 v · 23 min"));
        Assert.Equal(300f, d.Size.W, 0.01);
    }

    [Fact]
    public void Red_state_uses_acima_badge()
    {
        var s = Blue(); s.State = SignalState.Red; s.LapProjectionL = 2.42; s.TargetL = 2.35;
        var d = Build(s);
        Assert.True(Has(d, "ACIMA"));
        Assert.False(Has(d, "NO ALVO"));
        Assert.Contains(d.Commands.ToArray(), c => c.Kind == CommandKind.FillRoundedRect && c.Color == new Rgba(0xC7, 0x36, 0x2F, 255));
        Assert.Contains(d.Commands.ToArray(), c => c.Kind == CommandKind.FillRoundedRect && c.Color == new Rgba(235, 87, 87, 71));
        Assert.Equal(new Rgba(0xFF, 0x7A, 0x7A, 255), Text(d, "2.42").Color);
    }

    [Fact]
    public void Not_feasible_shows_title_subtitle_and_pit_badge()
    {
        var s = Blue(); s.State = SignalState.NotFeasible; s.TargetL = 2.01; s.FeasibleMinL = 2.23; s.LapProjectionL = 2.39;
        s.Insight = new Insight(InsightKind.StopRequired, 3.3);
        var d = Build(s);
        Assert.True(Has(d, "NÃO FACTÍVEL"));
        Assert.Equal(new Rgba(255, 255, 255, 255), Text(d, "NÃO FACTÍVEL").Color);
        Assert.True(Has(d, "alvo 2.01 · mín possível 2.23"));
        Assert.True(Has(d, "PIT"));
        Assert.Equal(new Rgba(0x0E, 0x10, 0x13, 255), Text(d, "PIT").Color);
        Assert.Contains(d.Commands.ToArray(), c => c.Kind == CommandKind.FillRoundedRect && c.Color == White);
        Assert.Equal(White, Text(d, "2.39").Color); // VOLTA neutral
        Assert.True(Has(d, "faltam 16.6 v"));
        Assert.True(Has(d, "Faltam 3.3 L"));
    }

    [Fact]
    public void No_baseline_practice_shows_dash_and_treino_sem_limite()
    {
        var s = Blue(); s.State = SignalState.NoBaseline; s.TargetL = double.NaN; s.IsPractice = true;
        s.Insight = new Insight(InsightKind.NoBaseline);
        var d = Build(s);
        Assert.Equal(new Rgba(0xA9, 0xB2, 0xBD, 255), Text(d, "—").Color);
        Assert.True(Has(d, "treino · sem limite"));
        Assert.True(Has(d, "Sem baseline para Ligier · Zolder Alt"));
        Assert.DoesNotContain(Texts(d), t => t.Text is "NO ALVO" or "ACIMA" or "PIT");
        Assert.Contains(d.Commands.ToArray(), c => c.Kind == CommandKind.FillRoundedRect && c.Color == new Rgba(255, 255, 255, 18));
        // not practice: just "sem limite"
        s.IsPractice = false;
        Assert.True(Has(Build(s), "sem limite"));
    }

    [Fact]
    public void Stint_mode_shows_stint_label()
    {
        var s = Blue(); s.IsStintMode = true; s.LapsRemaining = 25;
        var d = Build(s);
        Assert.True(Has(d, "STINT 25.0 v"));
        Assert.DoesNotContain(Texts(d), t => t.Text.StartsWith("faltam"));
    }

    [Fact]
    public void Estimate_prefixes_tilde()
    {
        var s = Blue(); s.TargetIsEstimate = true; s.TargetL = 2.43;
        Assert.True(Has(Build(s), "~2.43"));
    }

    [Fact]
    public void Invalid_last_lap_gets_asterisk()
    {
        var s = Blue(); s.LastLapInvalid = true;
        Assert.True(Has(Build(s), "2.39*"));
        Assert.False(Has(Build(Blue()), "2.39*"));
    }

    [Fact]
    public void Mean_from_baseline_is_italic()
    {
        var s = Blue(); s.MeanFromBaseline = true;
        Assert.True(Text(Build(s), "2.38").Italic);
        Assert.False(Text(Build(Blue()), "2.38").Italic);
    }

    [Fact]
    public void Insight_bold_prefix_split()
    {
        var d = Build(Blue());
        Assert.Equal(FontRole.BodyBold, Text(d, "Folga 1.8 L").Role);
        Assert.Equal(FontRole.Body, Text(d, " · pode atacar").Role);
        Assert.Equal(new Rgba(0xE3, 0xE7, 0xEC, 255), Text(d, "Folga 1.8 L").Color);
    }

    [Fact]
    public void Inactive_shows_only_fuel_row_dimmed()
    {
        var s = Blue(); s.State = SignalState.Inactive;
        var d = Build(s);
        Assert.True(Has(d, "COMB"));
        Assert.False(Has(d, "VOLTA"));
        Assert.False(Has(d, "ALVO"));
        Assert.Equal(128, Text(d, "COMB").Color.A);
        Assert.True(d.Size.H < Build(Blue()).Size.H / 2);
    }

    [Fact]
    public void Rec_pill_only_when_recording_and_debug_tag_when_enabled()
    {
        var s = Blue();
        var plain = Build(s);
        Assert.False(Has(plain, "REC"));
        Assert.False(Has(plain, "DBG"));
        s.Recording = true; s.RecValid = 4; s.RecTotal = 5;
        var rec = Build(s);
        Assert.True(Has(rec, "REC"));
        Assert.True(Has(rec, "4/5 válidas"));
        Assert.False(Has(rec, "DBG"));
        Assert.True(rec.Size.H > plain.Size.H);
        var dbg = Build(s, Opts(debug: true));
        Assert.True(Has(dbg, "DBG"));
        Assert.True(Text(dbg, "DBG").X < Text(dbg, "REC").X);
        // right aligned to the panel edge (300)
        var pills = dbg.Commands.ToArray().Where(c => c.Kind == CommandKind.FillRoundedRect && c.Color == new Rgba(10, 12, 16, 153)).ToArray();
        Assert.Equal(3, pills.Length); // panel + DBG + REC
        Assert.Equal(300f, pills.Max(p => p.Rect.X + p.Rect.W), 0.01);
    }

    [Fact]
    public void Scale_2_doubles_every_dimension()
    {
        var s = Blue(); s.Recording = true; s.RecValid = 4; s.RecTotal = 5;
        var one = Build(s);
        var two = Build(s, Opts(scale: 2));
        Assert.Equal(one.Size.W * 2, two.Size.W, 0.5);
        Assert.Equal(one.Size.H * 2, two.Size.H, 0.5);
        Assert.Equal(one.Commands.Length, two.Commands.Length);
        Assert.Equal(Text(one, "2.36").SizePx * 2, Text(two, "2.36").SizePx, 0.01);
    }

    [Fact]
    public void Opacity_options_affect_alpha_only()
    {
        var s = Blue(); s.Recording = true; s.RecValid = 1; s.RecTotal = 5;
        var a = Build(s, Opts(bg: 0.3, content: 1));
        var b = Build(s, Opts(bg: 0.9, content: 0.5));
        Assert.Equal(a.Size, b.Size);
        Assert.Equal(a.Commands.Length, b.Commands.Length);
        for (int i = 0; i < a.Commands.Length; i++)
        {
            Assert.Equal(a.Commands[i].Rect, b.Commands[i].Rect);
            Assert.Equal(a.Commands[i].Kind, b.Commands[i].Kind);
        }
        Assert.Equal(77, a.Commands[0].Color.A);   // 0.3 * 255
        Assert.Equal(230, b.Commands[0].Color.A);  // 0.9 * 255
        Assert.Equal(255, Text(a, "2.36").Color.A);
        Assert.Equal(128, Text(b, "2.36").Color.A);
        // the ALVO row tint is not content
        Assert.Contains(b.Commands.ToArray(), c => c.Color == new Rgba(47, 128, 237, 66));
    }

    [Fact]
    public void Edit_mode_adds_outline_handles_banner_and_footer()
    {
        var plain = Build(Blue(), Opts(scale: 2));
        var d = Build(Blue(), Opts(scale: 2, edit: true));
        var cmds = d.Commands.ToArray();
        Assert.Single(cmds, c => c.Kind == CommandKind.StrokeDashedRect);
        Assert.Equal(4, cmds.Count(c => c.Kind == CommandKind.FillRect && c.Rect.W == 7 && c.Rect.H == 7));
        Assert.Equal(4, cmds.Count(c => c.Kind == CommandKind.StrokeRect && c.Rect.W == 7));
        Assert.Contains(cmds, c => c.Kind == CommandKind.FillRoundedRect && c.Color == new Rgba(0x1E, 0x6B, 0xD6, 255) && c.Rect.W > 200);
        Assert.True(Has(d, "MODO EDIÇÃO"));
        Assert.True(Has(d, "arraste para mover · puxe um canto para redimensionar"));
        Assert.Equal(11f, Text(d, "MODO EDIÇÃO").SizePx); // fixed, not scaled
        Assert.True(Has(d, "Ctrl+Shift+F"));
        Assert.True(Has(d, " trava e volta ao modo click-through"));
        Assert.True(Has(d, "X 1840 · Y 980 · escala 200%"));
        Assert.True(d.Size.H > plain.Size.H);
    }

    [Fact]
    public void Dpi_scales_the_panel_with_the_user_scale_and_the_chrome_alone()
    {
        var o = Opts(scale: 2, edit: true) with { DpiScale = 1.5 };
        var d = Build(Blue(), o);
        var cmds = d.Commands.ToArray();
        var panel = cmds.First(c => c.Kind == CommandKind.FillRoundedRect);
        var offset = OverlayLayout.PanelOffset(true, 1.5);
        Assert.Equal((7 * 1.5f, 31 * 1.5f), (offset.W, offset.H));
        Assert.Equal((offset.W, offset.H), (panel.Rect.X, panel.Rect.Y));
        Assert.Equal(300 * 3f, panel.Rect.W, 0.01);                      // panel: Scale × DPI
        Assert.Equal(11 * 1.5f, Text(d, "MODO EDIÇÃO").SizePx, 0.01);   // chrome: DPI only
        Assert.Equal(4, cmds.Count(c => c.Kind == CommandKind.FillRect && Math.Abs(c.Rect.W - 7 * 1.5f) < 0.01));
        Assert.True(Has(d, "X 1840 · Y 980 · escala 200%"));             // the user's scale, not Scale × DPI
        Assert.Equal(default, OverlayLayout.PanelOffset(false, 1.5));
    }

    [Fact]
    public void Hidden_insight_line_shrinks_height()
    {
        var withLine = Build(Blue());
        var without = Build(Blue(), Opts(insight: false));
        Assert.True(without.Size.H < withLine.Size.H);
        Assert.False(Has(without, "Folga 1.8 L"));
    }

    [Fact]
    public void Font_weights_follow_the_mocks()
    {
        var s = Blue(); s.Recording = true; s.RecValid = 4; s.RecTotal = 5;
        var d = Build(s, Opts(pace: true, debug: true));
        Assert.Equal(600, Text(d, "COMB").Weight);
        Assert.Equal(600, Text(d, "41.8").Weight);
        Assert.Equal(400, Text(d, "L").Weight);
        Assert.Equal(400, Text(d, "17.3 v · 23 min").Weight);
        Assert.Equal(600, Text(d, "VOLTA").Weight);
        Assert.Equal(700, Text(d, "2.36").Weight);
        Assert.Equal(500, Text(d, "2.39").Weight);
        Assert.Equal(500, Text(d, "2.38").Weight);
        Assert.Equal(700, Text(d, "2.49").Weight);
        Assert.Equal(400, Text(d, "faltam 16.6 v").Weight);
        Assert.Equal(700, Text(d, "NO ALVO").Weight);
        Assert.Equal(700, Text(d, "Folga 1.8 L").Weight);
        Assert.Equal(400, Text(d, " · pode atacar").Weight);
        Assert.Equal(400, Text(d, "ÚLT 79.51 s · MÉD 80.14 · base 80.12").Weight);
        Assert.Equal(700, Text(d, "REC").Weight);
        Assert.Equal(400, Text(d, "4/5 válidas").Weight);
        Assert.Equal(700, Text(d, "DBG").Weight);
        var nf = Blue(); nf.State = SignalState.NotFeasible; nf.TargetL = 2.01; nf.FeasibleMinL = 2.23;
        var f = Build(nf);
        Assert.Equal(800, Text(f, "NÃO FACTÍVEL").Weight);
        Assert.Equal(800, Text(f, "PIT").Weight);
        Assert.Equal(400, Text(f, "alvo 2.01 · mín possível 2.23").Weight);
        var e = Build(Blue(), Opts(edit: true));
        Assert.Equal(600, Text(e, "MODO EDIÇÃO").Weight);
        Assert.Equal(600, Text(e, "Ctrl+Shift+F").Weight);
        Assert.Equal(400, Text(e, "X 1840 · Y 980 · escala 100%").Weight);
    }

    [Fact]
    public void Insight_regular_run_continues_bold_run_and_svg_flows_it_in_one_text()
    {
        var d = Build(Blue());
        Assert.False(Text(d, "Folga 1.8 L").Continuation);
        Assert.True(Text(d, " · pode atacar").Continuation);
        var svg = SvgRenderer.Render(d);
        Assert.Contains("<tspan", svg);
        Assert.Contains("Folga 1.8 L<tspan", svg);
    }

    [Fact]
    public void Centre_text_sits_between_left_group_and_badge_with_equal_gaps()
    {
        var nf = Blue(); nf.State = SignalState.NotFeasible; nf.TargetL = 2.01; nf.FeasibleMinL = 2.23;
        var d = Build(nf);
        float leftEnd = Text(d, "alvo 2.01 · mín possível 2.23").X + M.Width("alvo 2.01 · mín possível 2.23", FontRole.Value, 400, 8.5f, 0);
        var c = Text(d, "faltam 14.8 v".Replace("14.8", "16.6"));
        float centreEnd = c.X + M.Width("faltam 16.6 v", FontRole.Value, 400, 11, 0);
        float badgeX = Text(d, "PIT").X - 6;
        Assert.True(c.X - leftEnd >= 3.5f, $"left gap {c.X - leftEnd}");
        Assert.True(badgeX - centreEnd >= 3.5f, $"right gap {badgeX - centreEnd}");
        Assert.Equal(c.X - leftEnd, badgeX - centreEnd, 0.1);
    }

    [Fact]
    public void Long_insight_is_truncated_with_ellipsis_inside_the_panel()
    {
        var s = Blue(); s.State = SignalState.NoBaseline; s.Insight = new Insight(InsightKind.NoBaseline);
        var d = new DrawList();
        OverlayLayout.Build(in s, "Ferrari 296 GT3 Evo Customer", "Circuit de Spa-Francorchamps Grand Prix", Opts(), M, d);
        var t = Texts(d).Last(x => x.Text.StartsWith("Sem baseline"));
        Assert.EndsWith("…", t.Text);
        Assert.True(t.Cmd.X + M.Width(t.Text, FontRole.Body, 400, 11, 0) <= 289.01f);
    }

    [Fact]
    public void Pace_line_shows_times_when_enabled()
    {
        var d = Build(Blue(), Opts(pace: true));
        Assert.True(Has(d, "ÚLT 79.51 s · MÉD 80.14 · base 80.12"));
        Assert.True(d.Size.H > Build(Blue()).Size.H);
    }

    [Fact]
    public void Build_allocates_nothing_after_warmup()
    {
        var s = Blue(); s.Recording = true; s.RecValid = 4; s.RecTotal = 5;
        var small = Opts(); var edit = Opts(scale: 2, edit: true, debug: true, pace: true);
        var d = new DrawList();
        for (int i = 0; i < 20; i++)
        {
            OverlayLayout.Build(in s, "Ligier", "Zolder Alt", small, M, d);
            OverlayLayout.Build(in s, "Ligier", "Zolder Alt", edit, M, d);
        }
        var m = AllocationMeasure.Start();
        for (int i = 0; i < 200; i++)
        {
            OverlayLayout.Build(in s, "Ligier", "Zolder Alt", small, M, d);
            OverlayLayout.Build(in s, "Ligier", "Zolder Alt", edit, M, d);
        }
        m.AssertNone();
    }

    [Fact]
    public void Content_hash_changes_only_when_visible_output_changes()
    {
        var s = Blue();
        long h = Build(s).ContentHash;
        var same = Blue(); same.FuelL = 41.8004; // prints 41.8
        Assert.Equal(h, Build(same).ContentHash);
        var diff = Blue(); diff.FuelL = 41.9;
        Assert.NotEqual(h, Build(diff).ContentHash);
        Assert.NotEqual(h, Build(s, Opts(bg: 0.5)).ContentHash);
        var red = Blue(); red.State = SignalState.Red;
        Assert.NotEqual(h, Build(red).ContentHash);
    }

    [Fact]
    public void Svg_renderer_emits_text_and_shapes()
    {
        var svg = SvgRenderer.Render(Build(Blue()));
        Assert.StartsWith("<svg", svg);
        Assert.Contains("NO ALVO", svg);
        Assert.Contains("JetBrains Mono", svg);
        Assert.Contains("</svg>", svg);
    }
}
