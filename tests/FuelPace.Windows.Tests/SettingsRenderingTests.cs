using System.Windows;
using System.Windows.Media;
using FuelPace.Agent.Overlay;
using FuelPace.App.Overlay;
using FuelPace.Settings;

namespace FuelPace.Windows.Tests;

/// <summary>The Settings preview measures and draws text like the Agent: the same embedded faces, weights, letter spacing,
/// italic and continuation runs.</summary>
public sealed class SettingsRenderingTests
{
    static List<GlyphRunDrawing> Runs(Drawing? d, List<GlyphRunDrawing>? acc = null)
    {
        acc ??= [];
        switch (d)
        {
            case DrawingGroup g: foreach (var c in g.Children) Runs(c, acc); break;
            case GlyphRunDrawing r: acc.Add(r); break;
        }
        return acc;
    }

    static List<GlyphRunDrawing> Draw(DrawList list)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) WpfDrawListRenderer.Draw(dc, list, EmbeddedFonts.Shared, 1.0);
        return Runs(VisualTreeHelper.GetDrawing(v));
    }

    static double Advance(GlyphRun r) => r.AdvanceWidths.Sum();

    [Fact]
    public void Faces_are_the_embedded_files_with_the_exact_weights()
    {
        Sta.Run(() =>
        {
            var f = EmbeddedFonts.Shared;
            foreach (int w in new[] { 400, 600, 700, 800 })
            {
                var t = f.Face(FontRole.Label, w);
                Assert.Equal(w, t.Weight.ToOpenTypeWeight());
                Assert.StartsWith("Inter-", Path.GetFileName(t.FontUri.OriginalString), StringComparison.OrdinalIgnoreCase);
            }
            foreach (int w in new[] { 400, 500, 600, 700 })
            {
                var t = f.Face(FontRole.Value, w);
                Assert.Equal(w, t.Weight.ToOpenTypeWeight());
                Assert.StartsWith("JetBrainsMono-", Path.GetFileName(t.FontUri.OriginalString), StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal(800, f.Face(FontRole.Badge, 900).Weight.ToOpenTypeWeight()); // nearest available, as the Agent
            Assert.Equal(StyleSimulations.ItalicSimulation, f.Face(FontRole.Body, 400, italic: true).StyleSimulations);
        });
    }

    [Fact]
    public void Measurer_agrees_with_the_Agent_measurer()
    {
        Sta.Run(() =>
        {
            using var fonts = new DWriteFonts();
            var agent = new DWriteTextMeasurer(fonts);
            var mine = new WpfTextMeasurer(EmbeddedFonts.Shared);
            (string Text, FontRole Role, int Weight, float Size, float Spacing)[] runs =
            [
                ("41.8", FontRole.Value, 600, 21, 0), ("17.3 v · 23 min", FontRole.Value, 400, 12, 0),
                ("2.36", FontRole.ValueBold, 700, 16, 0), ("COMB", FontRole.Label, 600, 9, 0.1f),
                ("NO ALVO", FontRole.Badge, 700, 8, 0.08f), ("NÃO FACTÍVEL", FontRole.Badge, 800, 12, 0),
                ("pode atacar", FontRole.Body, 400, 11, 0), ("Folga 1.8 L", FontRole.BodyBold, 700, 11, 0),
            ];
            foreach (var r in runs)
            {
                float a = agent.Width(r.Text, r.Role, r.Weight, r.Size, r.Spacing);
                float m = mine.Width(r.Text, r.Role, r.Weight, r.Size, r.Spacing);
                // DirectWrite also applies kerning, which the glyph runs here do not: about a pixel at most on these runs
                // ("NO ALVO": the L-V pair). Monospaced runs have no kerning and match exactly.
                float tolerance = r.Role is FontRole.Value or FontRole.ValueBold ? 0.05f : 1.25f;
                Assert.True(Math.Abs(a - m) <= tolerance, $"'{r.Text}' {r.Role} {r.Weight}: Agent {a}, Settings {m}");
            }
            foreach (var role in new[] { FontRole.Label, FontRole.Value })
                Assert.Equal(agent.Ascent(role, 20), mine.Ascent(role, 20), 1);
            float plain = mine.Width("COMB", FontRole.Label, 600, 9, 0);
            Assert.Equal(plain + 4 * 0.1f * 9, mine.Width("COMB", FontRole.Label, 600, 9, 0.1f), 3);
            Assert.True(mine.Width("NÃO FACTÍVEL", FontRole.Badge, 800, 12, 0) > mine.Width("NÃO FACTÍVEL", FontRole.Badge, 700, 12, 0));
        });
    }

    [Fact]
    public void Text_runs_keep_weight_italic_spacing_and_flow_continuations()
    {
        Sta.Run(() =>
        {
            var list = new DrawList();
            var white = Rgba.Hex(0xFFFFFF);
            list.Text("Folga 1.8 L", FontRole.BodyBold, 700, 11, 0, false, white, 10, 20.4f);
            list.Text(" · pode atacar", FontRole.Body, 400, 11, 0, false, white, 999, 20.4f, continuation: true);
            list.Text("COMB", FontRole.Label, 600, 9, 0.1f, true, white, 10, 40);
            var runs = Draw(list);
            Assert.Equal(3, runs.Count);

            var (bold, cont, label) = (runs[0].GlyphRun, runs[1].GlyphRun, runs[2].GlyphRun);
            Assert.Equal(700, bold.GlyphTypeface.Weight.ToOpenTypeWeight());
            Assert.Equal(400, cont.GlyphTypeface.Weight.ToOpenTypeWeight());
            Assert.Equal(new Point(10, 20), bold.BaselineOrigin); // baselines snap to whole pixels, as in the Agent
            Assert.Equal(10 + Advance(bold), cont.BaselineOrigin.X, 3); // the continuation's X is ignored
            Assert.Equal(20, cont.BaselineOrigin.Y);

            Assert.Equal(600, label.GlyphTypeface.Weight.ToOpenTypeWeight());
            Assert.Equal(StyleSimulations.ItalicSimulation, label.GlyphTypeface.StyleSimulations);
            Assert.Equal(EmbeddedFonts.Shared.Width("COMB", FontRole.Label, 600, 9, 0.1f), Advance(label), 3);
        });
    }

    [Fact]
    public void Preview_element_sizes_to_the_list_and_renders_the_overlay()
    {
        Sta.Run(() =>
        {
            var p = OverlayPreviews.All[0];
            var list = new DrawList();
            var s = p.State;
            OverlayLayout.Build(in s, OverlayPreviews.Car, OverlayPreviews.Track, p.Options, new WpfTextMeasurer(EmbeddedFonts.Shared), list);
            var e = new WpfDrawListRenderer { List = list };
            e.Measure(new Size(1000, 1000));
            Assert.Equal(list.Size.W, e.DesiredSize.Width, 2);
            Assert.Equal(list.Size.H, e.DesiredSize.Height, 2);
        });
    }
}
