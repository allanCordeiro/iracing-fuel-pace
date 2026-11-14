using FuelPace.App.Overlay;
using FuelPace.Core.Display;
using FuelPace.Core.Strategy;
using FuelPace.Tests.Support;

namespace FuelPace.App.Tests.Overlay;

/// <summary>PRD v3 §4 and §6 (criterion V3-7): the v3 panel geometry, its bands per state and its scaling.</summary>
[Collection(PerfCollection.Name)]
public sealed class GlanceLayoutTests
{
    static readonly FixedMetricsMeasurer M = new();
    static readonly Rgba PanelBg = new(10, 12, 16, 153); // rgba(10,12,16,0.60)
    static readonly Rgba White = Rgba.Hex(0xF2F4F7), Secondary = Rgba.Hex(0xD5DAE0), Label = Rgba.Hex(0xA9B2BD);

    static OverlayViewOptions Opts(double scale = 1, double bg = 0.6, double content = 1, bool pace = false, bool edit = false,
        bool debug = false, double dpi = 1) =>
        new(scale, bg, content, pace, edit, debug, "Ctrl+Shift+F", 1840, 980, dpi);

    // V3-3 values (PRD v3 §10)
    internal static GlanceView Red() => new()
    {
        Mode = GlanceMode.Full, Decision = DecisionKind.Economize, DecisionValue = 0.08f, ComplementPct = -3, ComplementSec = float.NaN,
        ShowBar = true, BarSteps = 20, BarMarkerQ = 8, BarGhostQ = 9,
        LapProj = 2.38f, LapLast = 2.39f, LapAvg = 2.37f, ShowAvg = true,
        Fuel = 74.0f, LapsPossible = 31, LapsRemaining = 32, TargetLabel = TargetLabel.Alvo, Target = 2.30f, FeasibleMin = float.NaN,
        MinutesPossible = 41, LastLapTimeS = 79.51f, MeanLapTimeS = 80.14f, BaselineLapTimeS = 80.12f,
    };

    internal static GlanceView Blue()
    {
        var v = Red();
        v.Decision = DecisionKind.Ok; v.DecisionValue = 2.7f; v.ComplementPct = 0; v.BarMarkerQ = -9; v.BarGhostQ = -8;
        v.LapProj = 2.36f; v.LapLast = 2.37f; v.LapsRemaining = 30; v.TargetLabel = TargetLabel.Ate; v.Target = 2.45f;
        return v;
    }

    internal static GlanceView Pit()
    {
        var v = Red();
        v.Decision = DecisionKind.Pit; v.DecisionValue = 2.3f; v.ComplementPct = 0; v.ShowBar = false;
        v.BarMarkerQ = GlanceView.BarAbsent; v.BarGhostQ = GlanceView.BarAbsent;
        v.LapsRemaining = 34; v.TargetLabel = TargetLabel.PitMin; v.Target = 2.16f; v.FeasibleMin = 2.23f;
        return v;
    }

    internal static GlanceView Compact() => new()
    {
        Mode = GlanceMode.Compact, Decision = DecisionKind.None, DecisionValue = float.NaN, ComplementSec = float.NaN,
        BarMarkerQ = GlanceView.BarAbsent, BarGhostQ = GlanceView.BarAbsent,
        LapProj = 2.91f, LapLast = 2.91f, LapAvg = 2.92f, ShowAvg = true,
        Fuel = 74.0f, LapsPossible = 25, LapsRemaining = -1, Target = float.NaN, FeasibleMin = float.NaN, MinutesPossible = 45,
        Pills = StatusPills.Rec, RecValid = 4, RecTotal = 9,
        LastLapTimeS = float.NaN, MeanLapTimeS = float.NaN, BaselineLapTimeS = float.NaN,
    };

    static DrawList Build(in GlanceView v, OverlayViewOptions? o = null)
    {
        var d = new DrawList();
        GlanceLayout.Build(in v, o ?? Opts(), M, d);
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

    static float Width(string text, in DrawCommand c) => M.Width(text, c.Role, c.Weight, c.SizePx, c.LetterSpacingEm);

    static DrawCommand Panel(DrawList d) => d.Commands[0];

    static DrawCommand[] Fills(DrawList d, Rgba color) =>
        d.Commands.ToArray().Where(c => c.Kind == CommandKind.FillRoundedRect && c.Color == color).ToArray();

    // ---- V3-7: sizes ----

    [Fact]
    public void Full_panel_is_about_300_by_120()
    {
        var d = Build(Red());
        var p = Panel(d);
        Assert.Equal(CommandKind.FillRoundedRect, p.Kind);
        Assert.Equal(PanelBg, p.Color);
        Assert.Equal(6f, p.Radius, 0.01);
        Assert.InRange(p.Rect.W, 296f, 304f);
        Assert.InRange(p.Rect.H, 114f, 126f);
        Assert.Equal(new SizeF(p.Rect.W, p.Rect.H), d.Size); // no pills: the list is the panel
    }

    [Fact]
    public void Compact_panel_is_300_wide_and_at_most_70_tall()
    {
        var v = Compact(); v.Pills = StatusPills.None;
        var p = Panel(Build(v));
        Assert.Equal(300f, p.Rect.W, 0.01);
        Assert.True(p.Rect.H <= 70f, $"compact height {p.Rect.H}");
    }

    [Fact]
    public void Hiding_the_bar_removes_its_height_and_one_gap()
    {
        var off = Red(); off.ShowBar = false;
        float with = Panel(Build(Red())).Rect.H, without = Panel(Build(off)).Rect.H;
        Assert.Equal(18f + 6f, with - without, 0.01);
        Assert.DoesNotContain(Build(off).Commands.ToArray(), c => c.Color == GlanceTheme.BarBlue || c.Kind == CommandKind.FillTriangle);
    }

    [Fact]
    public void Pills_do_not_change_the_panel_and_take_no_space_when_absent()
    {
        var none = Build(Red());
        var v = Red(); v.Pills = StatusPills.Rec | StatusPills.BopMismatch | StatusPills.Caution; v.RecValid = 4; v.RecTotal = 9;
        var pills = Build(v);
        Assert.Equal(Panel(none).Rect, Panel(pills).Rect);
        Assert.Equal(Panel(none).Rect.H, none.Size.H, 0.01);
        Assert.Equal(Panel(none).Rect.H + 4 + 18, pills.Size.H, 0.01);
    }

    public static TheoryData<string> WidestCases() => ["red", "blue", "pit", "compact", "inactive", "stint", "seconds"];

    [Theory]
    [MemberData(nameof(WidestCases))]
    public void Widest_values_stay_inside_the_panel_and_do_not_overlap(string which)
    {
        var v = Red();
        v.DecisionValue = 0.50f; v.ComplementPct = -20; v.LapsRemaining = 999; v.Fuel = 100.0f; v.LapsPossible = 99;
        v.Target = 2.30f; v.IsEstimate = true; v.LapProj = 2.38f; v.LapLast = 2.39f; v.LapLastInvalid = true;
        switch (which)
        {
            case "blue": v.Decision = DecisionKind.Ok; v.DecisionValue = 99.9f; v.ComplementPct = 0; v.TargetLabel = TargetLabel.Ate; break;
            case "pit":
                v.Decision = DecisionKind.Pit; v.DecisionValue = 99.9f; v.ComplementPct = 0; v.ShowBar = false;
                v.TargetLabel = TargetLabel.PitMin; v.Target = 2.16f; v.FeasibleMin = 2.23f; break;
            case "compact": v = Compact(); v.Fuel = 100.0f; v.LapsPossible = 99; v.MinutesPossible = 999; break;
            case "inactive": v = Compact(); v.Mode = GlanceMode.Inactive; v.Fuel = 100.0f; v.LapsPossible = 99; v.MinutesPossible = 999; break;
            case "stint": v.IsStint = true; v.LapsRemaining = 999; break;
            case "seconds": v.ComplementSec = 9.9f; break;
        }
        var d = Build(v, Opts(pace: true));
        var p = Panel(d).Rect;
        float left = p.X + 9, right = p.X + p.W - 9;
        var boxes = new List<(string T, float X0, float X1, float Y)>();
        foreach (var (t, c) in Texts(d))
        {
            if (c.BaselineY > p.Y + p.H) continue; // pills
            float w = Width(t, c);
            Assert.True(c.X >= left - 0.01f, $"'{t}' starts at {c.X}, inner edge {left}");
            Assert.True(c.X + w <= right + 0.01f, $"'{t}' ends at {c.X + w}, inner edge {right}");
            boxes.Add((t, c.X, c.X + w, c.BaselineY));
        }
        // runs that share a row (similar baseline) must not overlap
        foreach (var a in boxes)
            foreach (var b in boxes)
            {
                if (ReferenceEquals(a.T, b.T) || a.X0 >= b.X0 || Math.Abs(a.Y - b.Y) > 6) continue;
                Assert.True(a.X1 <= b.X0 + 0.01f, $"'{a.T}' [{a.X0},{a.X1}] overlaps '{b.T}' [{b.X0},{b.X1}]");
            }
    }

    [Fact]
    public void Scale_2_5_is_proportional_within_1px_per_element()
    {
        var v = Red(); v.Pills = StatusPills.Rec | StatusPills.BopMismatch | StatusPills.Caution; v.RecValid = 4; v.RecTotal = 9;
        foreach (var view in new[] { v, Blue(), Pit(), Compact() })
        {
            var one = Build(view, Opts(pace: true, debug: true));
            var big = Build(view, Opts(scale: 2.5, pace: true, debug: true));
            Assert.Equal(one.Commands.Length, big.Commands.Length);
            Assert.Equal(one.Size.W * 2.5f, big.Size.W, 1.0);
            Assert.Equal(one.Size.H * 2.5f, big.Size.H, 1.0);
            for (int i = 0; i < one.Commands.Length; i++)
            {
                DrawCommand a = one.Commands[i], b = big.Commands[i];
                Assert.Equal(a.Kind, b.Kind);
                Assert.Equal(a.Rect.X * 2.5f, b.Rect.X, 1.0);
                Assert.Equal(a.Rect.Y * 2.5f, b.Rect.Y, 1.0);
                Assert.Equal(a.Rect.W * 2.5f, b.Rect.W, 1.0);
                Assert.Equal(a.Rect.H * 2.5f, b.Rect.H, 1.0);
                Assert.Equal(a.SizePx * 2.5f, b.SizePx, 1.0);
                if (a.Kind == CommandKind.FillRoundedRect) Assert.Equal(a.Radius * 2.5f, b.Radius, 1.0);
                Assert.Equal(a.Color, b.Color);
            }
        }
    }

    // ---- F1: decision block ----

    [Fact]
    public void Red_block_shows_economize_number_and_percent()
    {
        var d = Build(Red());
        var block = Assert.Single(Fills(d, Rgba.From(235, 87, 87, 0.30)));
        Assert.Equal((9f, 7f, 282f, 35f), (block.Rect.X, block.Rect.Y, block.Rect.W, block.Rect.H));
        Assert.Equal(4f, block.Radius, 0.01);
        var selo = Assert.Single(Fills(d, Rgba.Hex(0xC7362F)));
        Assert.Equal(3f, selo.Radius, 0.01);
        var word = Text(d, "ECONOMIZE");
        Assert.Equal((FontRole.Badge, 800, 11f, 0.08f), (word.Role, word.Weight, word.SizePx, word.LetterSpacingEm));
        Assert.Equal(Rgba.Hex(0xFFFFFF), word.Color);
        Assert.Equal(selo.Rect.X + 7, word.X, 0.01);
        Assert.Equal(Width("ECONOMIZE", word) + 14, selo.Rect.W, 0.01);
        Assert.Equal(19f, selo.Rect.H, 0.01);
        var n = Text(d, "0.08");
        Assert.Equal((FontRole.ValueBold, 700, 25f), (n.Role, n.Weight, n.SizePx));
        Assert.Equal(White, n.Color);
        Assert.Equal(selo.Rect.X + selo.Rect.W + 9, n.X, 0.01);
        var pct = Text(d, "−3%");
        Assert.Equal((FontRole.Value, 600, 15f), (pct.Role, pct.Weight, pct.SizePx));
        Assert.Equal(Rgba.Hex(0xFFD9D9), pct.Color);
        Assert.Equal(284f, pct.X + Width("−3%", pct), 0.01); // right edge of the block content (291 − 7)
        Assert.False(Has(d, "FOLGA"));
    }

    [Fact]
    public void Red_complement_in_seconds_when_given()
    {
        var v = Red(); v.ComplementSec = 0.9f;
        var d = Build(v);
        Assert.True(Has(d, "+0.9 s"));
        Assert.False(Has(d, "−3%"));
    }

    [Fact]
    public void Blue_block_shows_ok_folga_liters()
    {
        var d = Build(Blue());
        Assert.Single(Fills(d, Rgba.From(47, 128, 237, 0.28)));
        Assert.Single(Fills(d, Rgba.Hex(0x1E6BD6)));
        var word = Text(d, "OK");
        Assert.Equal(Rgba.Hex(0xFFFFFF), word.Color);
        var label = Text(d, "FOLGA");
        Assert.Equal((FontRole.Label, 600, 11f, 0.06f), (label.Role, label.Weight, label.SizePx, label.LetterSpacingEm));
        Assert.Equal(Rgba.Hex(0xCFE2FF), label.Color);
        var n = Text(d, "2.7");
        Assert.Equal(label.X + Width("FOLGA", label) + 9, n.X, 0.01);
        var unit = Text(d, "L");
        Assert.Equal(12f, unit.SizePx);
        Assert.Equal(Rgba.Hex(0xCFE2FF), unit.Color);
        Assert.Equal(n.BaselineY, unit.BaselineY, 0.01);
        Assert.True(unit.X > n.X + Width("2.7", n));
        Assert.False(Has(d, "−3%"));
    }

    [Fact]
    public void Pit_block_shows_pit_faltam_liters_and_no_bar()
    {
        var d = Build(Pit());
        Assert.Single(Fills(d, Rgba.From(255, 255, 255, 0.14)));
        Assert.Single(Fills(d, Rgba.Hex(0xF2F4F7)));
        Assert.Equal(Rgba.Hex(0x0E1013), Text(d, "PIT").Color);
        Assert.Equal(Secondary, Text(d, "FALTAM").Color);
        Assert.Equal(Secondary, Text(d, "L").Color);
        Assert.True(Has(d, "2.3"));
        Assert.DoesNotContain(d.Commands.ToArray(), c => c.Color == GlanceTheme.BarBlue || c.Color == GlanceTheme.BarRed);
    }

    [Fact]
    public void Unknown_decision_value_shows_only_the_word_and_a_dash()
    {
        var v = Blue(); v.DecisionValue = float.NaN;
        var d = Build(v);
        Assert.True(Has(d, "OK"));
        Assert.True(Has(d, "—"));
        Assert.False(Has(d, "FOLGA"));
        Assert.False(Has(d, "L"));
    }

    // ---- F2: delta bar ----

    const float BarX = 12, BarHalf = 138, BarCenter = 150, BarTop = 7 + 35 + 6;

    [Fact]
    public void Bar_has_two_halves_target_line_ghost_and_marker()
    {
        var d = Build(Red());
        var blue = Assert.Single(Fills(d, Rgba.From(47, 128, 237, 0.42)));
        var red = Assert.Single(Fills(d, Rgba.From(235, 87, 87, 0.42)));
        Assert.Equal((BarX, BarTop + 7, BarHalf, 4f), (blue.Rect.X, blue.Rect.Y, blue.Rect.W, blue.Rect.H));
        Assert.Equal((BarCenter, BarTop + 7, BarHalf, 4f), (red.Rect.X, red.Rect.Y, red.Rect.W, red.Rect.H));
        Assert.Equal(2f, blue.Radius, 0.01);
        var target = Assert.Single(Fills(d, Rgba.Hex(0xF2F4F7)), c => c.Rect.W == 1.5f);
        Assert.Equal((BarCenter - 0.75f, BarTop, 18f), (target.Rect.X, target.Rect.Y, target.Rect.H));
        var ghost = Assert.Single(Fills(d, Rgba.From(242, 244, 247, 0.55)));
        Assert.Equal(BarCenter + 9 / 20f * BarHalf, ghost.Rect.X + ghost.Rect.W / 2, 0.01);
        Assert.Equal((1.5f, 12f, BarTop + 3), (ghost.Rect.W, ghost.Rect.H, ghost.Rect.Y));
        var marker = Assert.Single(Fills(d, Rgba.Hex(0xFF7A7A)), c => c.Rect.W == 6);
        Assert.Equal(BarCenter + 8 / 20f * BarHalf, marker.Rect.X + 3, 0.01);
        Assert.Equal((16f, BarTop + 1, 1.5f), (marker.Rect.H, marker.Rect.Y, marker.Radius));
        Assert.DoesNotContain(d.Commands.ToArray(), c => c.Kind == CommandKind.FillTriangle);
    }

    [Theory]
    [InlineData(-9, 0x6AAEFF)]
    [InlineData(0, 0x6AAEFF)]  // on the target: blue
    [InlineData(1, 0xFF7A7A)]
    public void Marker_color_follows_its_side(int q, uint rgb)
    {
        var v = Red(); v.BarMarkerQ = (sbyte)q;
        var d = Build(v);
        var marker = Assert.Single(d.Commands.ToArray(), c => c.Kind == CommandKind.FillRoundedRect && c.Rect.W == 6 && c.Rect.H == 16);
        Assert.Equal(Rgba.Hex(rgb), marker.Color);
        Assert.Equal(BarCenter + q / 20f * BarHalf, marker.Rect.X + 3, 0.01);
    }

    [Fact]
    public void Absent_marker_and_ghost_are_not_drawn()
    {
        var v = Red(); v.BarMarkerQ = GlanceView.BarAbsent;
        var d = Build(v);
        Assert.DoesNotContain(d.Commands.ToArray(), c => c.Rect.W == 6 && c.Rect.H == 16);
        Assert.Single(Fills(d, GlanceTheme.BarGhost));
        v.BarGhostQ = GlanceView.BarAbsent;
        Assert.Empty(Fills(Build(v), GlanceTheme.BarGhost));
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(-19, false)]
    [InlineData(20, true)]
    [InlineData(-20, true)]
    public void Triangle_only_when_saturated(int q, bool saturated)
    {
        var v = Red(); v.BarMarkerQ = (sbyte)q;
        var d = Build(v);
        var tris = d.Commands.ToArray().Where(c => c.Kind == CommandKind.FillTriangle).ToArray();
        if (!saturated)
        {
            Assert.Empty(tris);
            return;
        }
        var t = Assert.Single(tris);
        Assert.DoesNotContain(d.Commands.ToArray(), c => c.Kind == CommandKind.FillRoundedRect && c.Rect.W == 6 && c.Rect.H == 16);
        Assert.Equal(16f, t.Rect.H, 0.01);
        Assert.Equal(BarTop + 1, t.Rect.Y, 0.01);
        if (q > 0)
        {
            Assert.True(t.Radius > 0); // points right, outwards
            Assert.Equal(BarX + 2 * BarHalf, t.Rect.X + t.Rect.W, 0.01);
            Assert.Equal(Rgba.Hex(0xFF7A7A), t.Color);
        }
        else
        {
            Assert.True(t.Radius < 0);
            Assert.Equal(BarX, t.Rect.X, 0.01);
            Assert.Equal(Rgba.Hex(0x6AAEFF), t.Color);
        }
    }

    [Fact]
    public void Bar_uses_the_view_steps()
    {
        var v = Red(); v.BarSteps = 10; v.BarMarkerQ = 5;
        var marker = Build(v).Commands.ToArray().Single(c => c.Kind == CommandKind.FillRoundedRect && c.Rect.W == 6);
        Assert.Equal(BarCenter + BarHalf / 2, marker.Rect.X + 3, 0.01);
    }

    // ---- F3: support ----

    [Fact]
    public void Support_row_colors_follow_the_state()
    {
        var red = Build(Red());
        var volta = Text(red, "2.38");
        Assert.Equal((FontRole.Value, 600, 13f), (volta.Role, volta.Weight, volta.SizePx));
        Assert.Equal(Rgba.Hex(0xFF7A7A), volta.Color);
        Assert.Equal(Rgba.Hex(0xFFA8A8), Text(red, "2.39").Color);
        Assert.Equal(500, Text(red, "2.39").Weight);
        Assert.Equal(Secondary, Text(red, "2.37").Color);
        var lbl = Text(red, "VOLTA");
        Assert.Equal((FontRole.Label, 600, 8f, 0.1f), (lbl.Role, lbl.Weight, lbl.SizePx, lbl.LetterSpacingEm));
        Assert.Equal(Label, lbl.Color);
        Assert.Equal(lbl.X + Width("VOLTA", lbl) + 5, volta.X, 0.01);
        Assert.Equal(volta.X + Width("2.38", volta) + 17, Text(red, "ÚLT").X, 0.01);

        var blue = Build(Blue());
        Assert.Equal(Rgba.Hex(0x6AAEFF), Text(blue, "2.36").Color);
        Assert.Equal(Rgba.Hex(0x9CC8FF), Text(blue, "2.37").Color);

        var pit = Build(Pit());
        Assert.Equal(Secondary, Text(pit, "2.38").Color);
        Assert.Equal(Secondary, Text(pit, "2.39").Color);

        var compact = Build(Compact());
        Assert.All(Texts(compact).Where(t => t.Text == "2.91"), t => Assert.Equal(Secondary, t.Cmd.Color));
    }

    [Fact]
    public void Support_row_marks_invalid_last_italic_average_and_hides_average()
    {
        var v = Red(); v.LapLastInvalid = true; v.LapAvgFromBaseline = true;
        var d = Build(v);
        Assert.True(Has(d, "2.39*"));
        Assert.True(Text(d, "2.37").Italic);
        Assert.False(Text(d, "2.38").Italic);
        v.ShowAvg = false;
        var hidden = Build(v);
        Assert.False(Has(hidden, "MÉD"));
        Assert.False(Has(hidden, "2.37"));
        var nan = Red(); nan.LapProj = float.NaN; nan.LapLast = float.NaN;
        Assert.Equal(2, Texts(Build(nan)).Count(t => t.Text == "—"));
    }

    // ---- divider + F4 ----

    [Fact]
    public void Divider_sits_4px_above_the_autonomy_row()
    {
        var d = Build(Red());
        var div = Assert.Single(Fills(d, Rgba.From(169, 178, 189, 0.22)));
        Assert.Equal((9f, 1f, 282f), (div.Rect.X, div.Rect.H, div.Rect.W));
        var fuel = Text(d, "74.0");
        Assert.Equal(div.Rect.Y + 1 + 4 + M.Ascent(FontRole.ValueBold, 15), fuel.BaselineY, 0.01);
    }

    [Fact]
    public void Autonomy_row_red_and_blue()
    {
        var d = Build(Red());
        var fuel = Text(d, "74.0");
        Assert.Equal((FontRole.Value, 600, 12f), (fuel.Role, fuel.Weight, fuel.SizePx)); // V3-3 values fit at full size
        Assert.Equal(White, fuel.Color);
        Assert.Equal(11f, fuel.X, 0.01);
        var rest = Text(d, " L · dá 31 v · faltam 32");
        Assert.True(rest.Continuation);
        Assert.Equal((FontRole.Value, 400, fuel.SizePx), (rest.Role, rest.Weight, rest.SizePx));
        Assert.Equal(Secondary, rest.Color);
        Assert.Equal(fuel.X + Width("74.0", fuel), rest.X, 0.01);
        var val = Text(d, "2.30");
        Assert.Equal((FontRole.ValueBold, 700, 15f), (val.Role, val.Weight, val.SizePx));
        Assert.Equal(289f, val.X + Width("2.30", val), 0.01);
        var lbl = Text(d, "ALVO");
        Assert.Equal((FontRole.Label, 8f, Label), (lbl.Role, lbl.SizePx, lbl.Color));
        Assert.Equal(val.X - 4, lbl.X + Width("ALVO", lbl), 0.01);
        Assert.Equal(fuel.BaselineY, val.BaselineY, 0.01);

        var b = Build(Blue());
        Assert.True(Has(b, "ATÉ"));
        Assert.True(Has(b, "2.45"));
        Assert.Equal(12f, Text(b, " L · dá 31 v · faltam 30").SizePx);
        Assert.Equal(15f, Text(b, "2.45").SizePx);
        Assert.False(Has(b, "ALVO"));
    }

    [Fact]
    public void Autonomy_row_pit_takes_two_lines_at_full_size()
    {
        var d = Build(Pit());
        var p = Panel(d).Rect;
        Assert.InRange(p.H, 114f, 126f); // screen 11: the second line uses the height the missing bar frees
        Assert.Equal(Panel(Build(Red())).Rect.H - 18 - 6 + 15 + 6, p.H, 0.01);
        var fuel = Text(d, "74.0");
        var rest = Text(d, " L · dá 31 v");
        var alvo = Text(d, "alvo 2.16 ·");
        var min = Text(d, "MÍN");
        var val = Text(d, "2.23");
        var laps = Text(d, "faltam 34");
        Assert.Equal((12f, 12f, 12f), (fuel.SizePx, rest.SizePx, laps.SizePx));
        Assert.Equal((FontRole.Value, 11f, Label), (alvo.Role, alvo.SizePx, alvo.Color));
        Assert.Equal((15f, 8f), (val.SizePx, min.SizePx));
        // line 1: fuel and laps possible on the left; alvo · MÍN value on the right
        Assert.True(rest.Continuation);
        Assert.Equal(fuel.BaselineY, val.BaselineY, 0.01);
        Assert.Equal(fuel.BaselineY, alvo.BaselineY, 0.01);
        Assert.Equal(289f, val.X + Width("2.23", val), 0.01);
        Assert.Equal(val.X - 4, min.X + Width("MÍN", min), 0.01);
        Assert.Equal(min.X - 4, alvo.X + Width("alvo 2.16 ·", alvo), 0.01);
        Assert.True(rest.X + Width(" L · dá 31 v", rest) + 8 <= alvo.X);
        // line 2: the laps left, under the fuel
        Assert.Equal((11f, Secondary, false), (laps.X, laps.Color, laps.Continuation));
        Assert.Equal(fuel.BaselineY + 15 + 6, laps.BaselineY, 0.01);
        Assert.True(laps.BaselineY < p.Y + p.H - 7);
        var stint = Pit(); stint.IsStint = true; stint.LapsRemaining = 12;
        Assert.True(Has(Build(stint), "stint 12"));
    }

    static float AutonomySize(GlanceView v)
    {
        var d = Build(v);
        float y = Panel(d).Rect.Y + 60;
        return Texts(d).First(t => t.Cmd.Role == FontRole.Value && t.Cmd.Weight == 600 && t.Cmd.Color == White && t.Cmd.BaselineY > y).Cmd.SizePx;
    }

    static GlanceView Widest(GlanceView v)
    {
        v.LapsRemaining = 999; v.Fuel = 100.0f; v.LapsPossible = 99; v.IsEstimate = true;
        return v;
    }

    [Fact]
    public void Autonomy_has_two_sizes_full_when_it_fits_and_one_fallback_otherwise()
    {
        // the V3-3 mock values draw at the full 12 px
        Assert.Equal(12f, AutonomySize(Red()));
        Assert.Equal(12f, AutonomySize(Blue()));
        Assert.Equal(12f, AutonomySize(Pit()));

        // within one fit class the size does not move: faltam 32 vs 31, the "~" on/off while it still fits (ATÉ is short)
        var r31 = Red(); r31.LapsRemaining = 31;
        Assert.Equal(AutonomySize(Red()), AutonomySize(r31));
        var blueEst = Blue(); blueEst.IsEstimate = true;
        Assert.True(Has(Build(blueEst), "~2.45"));
        Assert.Equal(12f, AutonomySize(blueEst));

        // every overflowing row shares the one fallback size of the widest template
        float fallback = AutonomySize(Widest(Red()));
        Assert.InRange(fallback, 9f, 11.99f);
        var redEst = Red(); redEst.IsEstimate = true;                       // "~2.30" next to faltam 32 overflows
        Assert.Equal(fallback, AutonomySize(redEst));
        var big = Red(); big.LapsRemaining = 999;
        Assert.Equal(fallback, AutonomySize(big));
        var stint = Widest(Red()); stint.IsStint = true;
        Assert.Equal(fallback, AutonomySize(stint));
        Assert.Equal(fallback, AutonomySize(Widest(Blue())));

        // PIT: the widest template fits its two-line arrangement, so it stays at 12 px
        Assert.Equal(12f, AutonomySize(Widest(Pit())));
    }

    [Fact]
    public void Bar_is_drawn_only_for_economize_and_ok()
    {
        var pit = Pit(); pit.ShowBar = true; pit.BarMarkerQ = 3; pit.BarGhostQ = 2;
        var d = Build(pit);
        Assert.DoesNotContain(d.Commands.ToArray(), c => c.Color == GlanceTheme.BarBlue || c.Color == GlanceTheme.BarGhost);
        Assert.Equal(Panel(Build(Pit())).Rect.H, Panel(d).Rect.H, 0.01);
        Assert.Single(Fills(Build(Blue()), GlanceTheme.BarBlue));
    }

    [Fact]
    public void Inactive_dimming_does_not_dim_the_edit_handles()
    {
        var v = Compact(); v.Mode = GlanceMode.Inactive;
        var d = Build(v, Opts(edit: true));
        var handles = d.Commands.ToArray().Where(c => c.Kind == CommandKind.FillRect).ToArray();
        Assert.Equal(4, handles.Length);
        Assert.All(handles, h => Assert.Equal(255, h.Color.A));
        Assert.Equal(128, Text(d, "74.0").Color.A);
    }

    [Fact]
    public void Estimate_gets_a_tilde_and_stint_replaces_faltam()
    {
        var v = Red(); v.IsEstimate = true; v.IsStint = true; v.LapsRemaining = 12;
        var d = Build(v);
        Assert.True(Has(d, "~2.30"));
        Assert.True(Has(d, " L · dá 31 v · stint 12"));
        var nan = Red(); nan.IsEstimate = true; nan.Target = float.NaN; nan.LapsPossible = -1;
        var n = Build(nan);
        Assert.True(Has(n, "—"));
        Assert.False(Has(n, "~—"));
        Assert.True(Has(n, " L · dá — v · faltam 32"));
    }

    [Fact]
    public void Pace_line_goes_under_the_autonomy_row_in_support_style()
    {
        var plain = Build(Red());
        var d = Build(Red(), Opts(pace: true));
        Assert.Equal(Panel(plain).Rect.H + 6 + 13, Panel(d).Rect.H, 0.01);
        var t = Text(d, "79.51 s");
        Assert.Equal((FontRole.Value, 13f, Secondary), (t.Role, t.SizePx, t.Color));
        Assert.True(t.BaselineY > Text(d, "74.0").BaselineY);
        Assert.True(Has(d, "80.14 s"));
        Assert.True(Has(d, "BASE"));
        Assert.True(Has(d, "80.12"));
        var nob = Red(); nob.BaselineLapTimeS = float.NaN;
        Assert.False(Has(Build(nob, Opts(pace: true)), "BASE"));
    }

    // ---- compact / inactive ----

    [Fact]
    public void Compact_shows_fuel_autonomy_divider_and_support()
    {
        var d = Build(Compact());
        var fuel = Text(d, "74.0");
        Assert.Equal((FontRole.Value, 600, 21f), (fuel.Role, fuel.Weight, fuel.SizePx));
        var unit = Text(d, "L");
        Assert.Equal((11f, Label), (unit.SizePx, unit.Color));
        var auto = Text(d, "dá 25 v · 45 min");
        Assert.Equal((FontRole.Value, 13f, Secondary), (auto.Role, auto.SizePx, auto.Color));
        Assert.Equal(289f, auto.X + Width("dá 25 v · 45 min", auto), 0.01);
        Assert.Single(Fills(d, GlanceTheme.Divider));
        Assert.True(Has(d, "VOLTA"));
        Assert.True(Has(d, "2.92"));
        foreach (var gone in new[] { "ALVO", "ATÉ", "MÍN", "OK", "ECONOMIZE", "PIT", "—" }) Assert.False(Has(d, gone), gone);
        Assert.Empty(Fills(d, GlanceTheme.BarBlue));
        var noMin = Compact(); noMin.MinutesPossible = -1;
        Assert.True(Has(Build(noMin), "dá 25 v"));
    }

    [Fact]
    public void Inactive_shows_only_the_first_compact_band_at_half_opacity()
    {
        var v = Compact(); v.Mode = GlanceMode.Inactive; v.Pills = StatusPills.None;
        var d = Build(v);
        Assert.Equal(7f + 21 + 7, Panel(d).Rect.H, 0.01);
        Assert.Equal(128, Text(d, "74.0").Color.A);
        Assert.Equal(128, Text(d, "dá 25 v · 45 min").Color.A);
        Assert.False(Has(d, "VOLTA"));
        Assert.Empty(Fills(d, GlanceTheme.Divider));
        Assert.Equal(PanelBg, Panel(d).Color); // the background keeps its own opacity
    }

    [Fact]
    public void Unknown_autonomy_shows_only_the_fuel()
    {
        // PRD v3 §3: "—" disappears. No sim (— L), electric car, or a practice start without any mean.
        foreach (var mode in new[] { GlanceMode.Inactive, GlanceMode.Compact })
        {
            var v = Compact(); v.Mode = mode; v.LapsPossible = -1; v.MinutesPossible = -1;
            var d = Build(v);
            Assert.True(Has(d, "74.0"));
            Assert.DoesNotContain(Texts(d), t => t.Text.Contains("dá") || t.Text.Contains("min"));
            Assert.Equal(mode == GlanceMode.Inactive ? 7f + 21 + 7 : Panel(Build(Compact())).Rect.H, Panel(d).Rect.H, 0.01);
        }
        var noSim = Compact(); noSim.Mode = GlanceMode.Inactive; noSim.Pills = StatusPills.None; noSim.Fuel = float.NaN; noSim.LapsPossible = -1; noSim.MinutesPossible = -1;
        Assert.Equal(["—", "L"], Texts(Build(noSim)).Select(t => t.Text));
    }

    [Fact]
    public void Inactive_through_the_engine_shows_fuel_and_autonomy()
    {
        // B1: in the box with a stint mean of 2.37 L at 80.14 s the real engine and builder give COMB and the autonomy
        var i = new StrategyInput
        {
            SessionTime = 100, FuelL = 74, LapDistPct = 0, LapProjectionL = double.NaN, LastLapL = 2.38, StintMeanL = 2.37,
            StintMeanLapS = 80.14, LastLapTimeS = double.NaN, SessionTimeRemain = double.NaN, LeaderPaceS = double.NaN,
            LeaderLapDistPct = double.NaN, EstLapTimeS = double.NaN, IsRaceSession = true, SessionLapsRemainEx = 19,
            Race = RaceKind.Laps, Inactive = true,
        };
        var h = new SignalHysteresis();
        var s = FuelStrategyEngine.Compute(in i, null, new StrategyConfig(), ref h);
        var v = GlanceViewBuilder.Build(in s, GlanceConfig.Default);
        var d = Build(v);
        Assert.Equal(GlanceMode.Inactive, v.Mode);
        Assert.Equal(128, Text(d, "74.0").Color.A);
        Assert.Equal(128, Text(d, "dá 31 v · 41 min").Color.A);
        Assert.DoesNotContain(Texts(d), t => t.Text.Contains('—'));
        Assert.Equal(7f + 21 + 7, Panel(d).Rect.H, 0.01);
    }

    [Fact]
    public void Content_opacity_affects_content_only()
    {
        var a = Build(Red(), Opts(bg: 0.3, content: 1));
        var b = Build(Red(), Opts(bg: 0.9, content: 0.5));
        Assert.Equal(a.Size, b.Size);
        Assert.Equal(77, Panel(a).Color.A);
        Assert.Equal(230, Panel(b).Color.A);
        Assert.Equal(255, Text(a, "0.08").Color.A);
        Assert.Equal(128, Text(b, "0.08").Color.A);
    }

    // ---- pills ----

    [Fact]
    public void Pills_sit_under_the_panel_right_aligned_in_order()
    {
        var v = Red(); v.Pills = StatusPills.Rec | StatusPills.BopMismatch | StatusPills.Caution; v.RecValid = 4; v.RecTotal = 9;
        var d = Build(v, Opts(debug: true));
        var p = Panel(d).Rect;
        var pills = d.Commands.ToArray().Skip(1).Where(c => c.Kind == CommandKind.FillRoundedRect && c.Color == PanelBg).ToArray();
        Assert.Equal(4, pills.Length);
        Assert.All(pills, c => Assert.Equal((p.Y + p.H + 4, 18f, 5f), (c.Rect.Y, c.Rect.H, c.Radius)));
        Assert.Equal(p.X + p.W, pills.Max(c => c.Rect.X + c.Rect.W), 0.01);
        string[] order = ["DBG", "REC 4/9", "BoP≠", "~ CAUTION"];
        var xs = order.Select(t => Text(d, t).X).ToArray();
        for (int i = 1; i < xs.Length; i++) Assert.True(xs[i - 1] < xs[i], string.Join(", ", xs));
        foreach (var t in order)
        {
            var c = Text(d, t);
            Assert.Equal((FontRole.Value, 600, 10f), (c.Role, c.Weight, c.SizePx));
        }
        var dot = Assert.Single(Fills(d, Rgba.Hex(0xFF5A5A)));
        Assert.Equal((6f, 6f, 3f), (dot.Rect.W, dot.Rect.H, dot.Radius));
        Assert.Equal(dot.Rect.X + 6 + 5, Text(d, "REC 4/9").X, 0.01);
        // gap of 4 between pills
        var sorted = pills.OrderBy(c => c.Rect.X).ToArray();
        for (int i = 1; i < sorted.Length; i++) Assert.Equal(sorted[i - 1].Rect.X + sorted[i - 1].Rect.W + 4, sorted[i].Rect.X, 0.01);
    }

    // ---- edit mode ----

    [Fact]
    public void Edit_box_wraps_the_panel_and_the_pills()
    {
        var v = Compact();
        var o = Opts(scale: 2, edit: true);
        var d = Build(v, o);
        var cmds = d.Commands.ToArray();
        var panel = cmds[0];
        var off = GlanceLayout.PanelOffset(true);
        Assert.Equal((7f, 31f), (off.W, off.H));
        Assert.Equal((off.W, off.H), (panel.Rect.X, panel.Rect.Y));
        float pillsBottom = panel.Rect.Y + panel.Rect.H + (4 + 18) * 2;
        var outline = Assert.Single(cmds, c => c.Kind == CommandKind.StrokeDashedRect);
        Assert.Equal(pillsBottom + 3, outline.Rect.Y + outline.Rect.H, 0.01);
        Assert.Equal(panel.Rect.Y - 3, outline.Rect.Y, 0.01);
        // the handles are the only FillRect squares (OverlayEditGeometry hit-tests them)
        var handles = cmds.Where(c => c.Kind == CommandKind.FillRect).ToArray();
        Assert.Equal(4, handles.Length);
        Assert.All(handles, h => Assert.Equal((7f, 7f), (h.Rect.W, h.Rect.H)));
        Assert.Equal(pillsBottom, handles.Max(h => h.Rect.Y), 0.01);
        var br = handles.OrderBy(h => h.Rect.X + h.Rect.Y).Last();
        Assert.Equal(OverlayHit.BottomRight, OverlayEditGeometry.HitTest(d, br.Rect.X + 3, br.Rect.Y + 3));
        Assert.Equal(OverlayHit.Caption, OverlayEditGeometry.HitTest(d, panel.Rect.X + 100, panel.Rect.Y + 20));
        Assert.True(Has(d, "MODO EDIÇÃO"));
        Assert.Equal(11f, Text(d, "MODO EDIÇÃO").SizePx);
        Assert.True(Has(d, "X 1840 · Y 980 · escala 200%"));
        Assert.True(Has(d, "Ctrl+Shift+F"));
        Assert.True(Has(d, " trava e volta ao modo click-through"));
        Assert.True(Has(d, "arraste para mover · puxe um canto para redimensionar"));
        Assert.Contains(cmds, c => c.Kind == CommandKind.FillRoundedRect && c.Color == new Rgba(0x1E, 0x6B, 0xD6, 255) && c.Rect.W > 200); // banner
        Assert.True(Text(d, "X 1840 · Y 980 · escala 200%").BaselineY > pillsBottom);
    }

    [Fact]
    public void Full_panel_draws_no_fill_rect_outside_edit_mode()
    {
        foreach (var v in new[] { Red(), Blue(), Pit(), Compact() })
            Assert.DoesNotContain(Build(v, Opts(pace: true)).Commands.ToArray(), c => c.Kind == CommandKind.FillRect);
    }

    [Fact]
    public void Dpi_scales_the_panel_with_the_user_scale_and_the_chrome_alone()
    {
        var d = Build(Red(), Opts(scale: 2, edit: true, dpi: 1.5));
        var panel = Panel(d);
        var off = GlanceLayout.PanelOffset(true, 1.5);
        Assert.Equal((7 * 1.5f, 31 * 1.5f), (off.W, off.H));
        Assert.Equal((off.W, off.H), (panel.Rect.X, panel.Rect.Y));
        Assert.Equal(300 * 3f, panel.Rect.W, 0.01);
        Assert.Equal(25 * 3f, Text(d, "0.08").SizePx, 0.01);
        Assert.Equal(11 * 1.5f, Text(d, "MODO EDIÇÃO").SizePx, 0.01);
        Assert.Equal(default, GlanceLayout.PanelOffset(false, 1.5));
    }

    [Fact]
    public void List_size_covers_every_command()
    {
        var v = Red(); v.Pills = StatusPills.Rec | StatusPills.BopMismatch | StatusPills.Caution; v.RecValid = 4; v.RecTotal = 9;
        foreach (var o in new[] { Opts(), Opts(scale: 2, edit: true, debug: true), Opts(scale: 0.5, edit: true) })
        {
            var d = Build(v, o);
            foreach (var c in d.Commands)
            {
                float x1 = c.Kind == CommandKind.Text ? c.X + Width(new string(d.TextOf(in c)), c) : c.Rect.X + c.Rect.W;
                float y1 = c.Kind == CommandKind.Text ? c.BaselineY : c.Rect.Y + c.Rect.H;
                Assert.True(x1 <= d.Size.W + 0.01f && y1 <= d.Size.H + 0.01f, $"{c.Kind} ends at ({x1},{y1}); size {d.Size}");
            }
        }
    }

    // ---- performance and dirty flag ----

    [Fact]
    public void Build_allocates_nothing_after_warmup()
    {
        var red = Red(); red.Pills = StatusPills.Rec | StatusPills.BopMismatch | StatusPills.Caution; red.RecValid = 4; red.RecTotal = 9;
        red.IsEstimate = true; red.BarMarkerQ = 20;
        var pit = Pit(); var compact = Compact();
        var small = Opts(pace: true); var edit = Opts(scale: 2, edit: true, debug: true, pace: true);
        var d = new DrawList();
        for (int i = 0; i < 20; i++)
        {
            GlanceLayout.Build(in red, small, M, d);
            GlanceLayout.Build(in pit, edit, M, d);
            GlanceLayout.Build(in compact, edit, M, d);
        }
        var m = AllocationMeasure.Start();
        for (int i = 0; i < 200; i++)
        {
            GlanceLayout.Build(in red, small, M, d);
            GlanceLayout.Build(in pit, edit, M, d);
            GlanceLayout.Build(in compact, edit, M, d);
        }
        m.AssertNone();
    }

    [Fact]
    public void Content_hash_changes_with_the_marker()
    {
        long h = Build(Red()).ContentHash;
        Assert.Equal(h, Build(Red()).ContentHash);
        var moved = Red(); moved.BarMarkerQ = 9;
        Assert.NotEqual(h, Build(moved).ContentHash);
    }

    [Fact]
    public void Svg_renders_the_saturation_triangle()
    {
        var v = Red(); v.BarMarkerQ = 20;
        var svg = SvgRenderer.Render(Build(v));
        Assert.StartsWith("<svg", svg);
        Assert.Contains("<polygon", svg);
        Assert.Contains("ECONOMIZE", svg);
        Assert.Contains("JetBrains Mono", svg);
        Assert.EndsWith("</svg>", svg.TrimEnd());
    }
}
