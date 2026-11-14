using FuelPace.Core.Display;

namespace FuelPace.App.Overlay;

/// <summary>
/// PRD v3 §4 and §6: lays the v3 overlay out as a <see cref="DrawList"/> from a <see cref="GlanceView"/> (300 px wide at
/// 1x; Scale × DpiScale for the panel, DpiScale only for the edit chrome, as <see cref="OverlayLayout"/>). Lives in App,
/// next to the DrawList and the text measurer, so the geometry is tested on Linux. Zero allocation once warmed up.
/// </summary>
/// <remarks>
/// Bands top to bottom: F1 decision block, F2 delta bar, F3 support (VOLTA / ÚLT / MÉD), divider, F4 autonomy and per-lap
/// target, optional pace line; the compact panel (no baseline) is fuel + autonomy, divider, F3; the inactive one is its first
/// band only, dimmed. Status pills hang under the panel. Inside the panel only rounded fills are used: plain
/// <see cref="CommandKind.FillRect"/> squares are the edit handles, which <see cref="OverlayEditGeometry"/> hit-tests.
/// </remarks>
public static class GlanceLayout
{
    public static void Build(in GlanceView v, OverlayViewOptions o, ITextMeasurer m, DrawList dest)
    {
        dest.Clear();
        new Builder(o, m, dest).Run(in v);
    }

    /// <summary>Where the panel's top-left corner sits inside the list (physical px) in edit mode; same chrome as v2.1.</summary>
    public static SizeF PanelOffset(bool editMode, double dpiScale = 1.0) =>
        editMode ? new SizeF(EditMargin * (float)dpiScale, (BannerH + BannerGap) * (float)dpiScale) : default;

    // 1x metrics (px): PRD v3 §4.1 and the v3 mocks ÷ 2. Text rows use line height 1.0.
    const float PanelW = 300, PadV = 7, PadH = 9, PanelRadius = 6, Gap = 6, RowInset = 2;
    const float InnerW = PanelW - 2 * PadH;

    // F1 · decision block
    const float BlockPadV = 5, BlockPadH = 7, BlockRadius = 4, NumberFont = 25, BlockH = BlockPadV + NumberFont + BlockPadV;
    const float SeloPadV = 4, SeloPadH = 7, SeloRadius = 3, SeloFont = 11, SeloSpacing = 0.08f;
    const float DecisionLabelFont = 11, DecisionLabelSpacing = 0.06f, UnitFont = 12, UnitGap = 4, ComplementFont = 15, BlockGap = 9;

    // F2 · delta bar
    const float BarH = 18, BarInset = 3, RailH = 4, TargetW = 1.5f, GhostW = 1.5f, GhostH = 12;
    const float MarkerW = 6, MarkerH = 16, MarkerRadius = 1.5f, TriangleW = 8;

    // F3 · support (and the pace line)
    const float SupportLabelFont = 8, SupportLabelSpacing = 0.1f, SupportFont = 13, SupportLabelGap = 5, SupportGroupGap = 17;

    // divider + F4 · autonomy
    const float DividerH = 1, DividerGap = 4, AutonomyFont = 12, TargetFont = 15, TargetGap = 4, PitTargetFont = 11, AutonomyMinGap = 8;
    const float AutonomyH = TargetFont;

    // compact
    const float CompactFuelFont = 21, CompactUnitFont = 11, CompactAutonomyFont = 13, CompactUnitGap = 4;

    // status pills
    const float PillGap = 4, PillPadV = 4, PillPadH = 7, PillRadius = 5, PillFont = 10, PillH = PillPadV + PillFont + PillPadV;
    const float DotSize = 6, DotGap = 5;

    // edit chrome (v2.1, DPI only)
    const float EditMargin = 7, BannerH = 21, BannerGap = 10, ChromeFont = 11;

    struct Builder
    {
        readonly OverlayViewOptions _o;
        readonly ITextMeasurer _m;
        readonly DrawList _d;
        readonly float _k, _c; // panel factor (Scale × DPI), chrome factor (DPI)
        float _x0, _right;      // inner edges of the panel
        double _op;

        public Builder(OverlayViewOptions o, ITextMeasurer m, DrawList d)
        {
            _o = o; _m = m; _d = d; _c = (float)o.DpiScale; _k = (float)o.Scale * _c; _op = o.ContentOpacity;
        }

        readonly float W(ReadOnlySpan<char> t, FontRole r, int weight, float size, float ls) => _m.Width(t, r, weight, size * _k, ls);

        readonly float T(ReadOnlySpan<char> t, FontRole r, int weight, float size, float ls, bool italic, Rgba c, float x, float baseline, bool cont = false)
        {
            _d.Text(t, r, weight, size * _k, ls, italic, c.WithOpacity(_op), x, baseline, cont);
            return W(t, r, weight, size, ls);
        }

        readonly float Asc(FontRole r, float size) => _m.Ascent(r, size * _k);

        /// <summary>Baseline of a 1.0-high line of <paramref name="size"/> centred on <paramref name="cy"/> (flex align-items: center).</summary>
        readonly float Centred(FontRole r, float size, float cy) => cy - size * _k / 2 + Asc(r, size);

        static int Cat(Span<char> dest, int pos, ReadOnlySpan<char> s)
        {
            int n = Math.Min(s.Length, dest.Length - pos);
            s[..n].CopyTo(dest[pos..]);
            return pos + n;
        }

        static int Int(int v, Span<char> dest) => v.TryFormat(dest, out int n, default, System.Globalization.CultureInfo.InvariantCulture) ? n : 0;

        public void Run(in GlanceView v)
        {
            float k = _k;
            bool edit = _o.EditMode;
            bool full = v.Mode == GlanceMode.Full, inactive = v.Mode == GlanceMode.Inactive;
            // PRD v3 §5.2: the bar exists only in the blue and red states (no reachable target to centre it on in PIT)
            bool bar = full && v.ShowBar && (v.Decision is DecisionKind.Economize or DecisionKind.Ok);
            bool pace = !inactive && _o.ShowPaceLine, twoLine = full && v.TargetLabel == TargetLabel.PitMin;
            if (inactive) _op *= 0.5;

            // Panel height (1x)
            float h = PadV;
            if (full)
            {
                h += BlockH;
                if (bar) h += Gap + BarH;
                h += Gap + SupportFont + Gap + DividerH + DividerGap + AutonomyH;
                if (twoLine) h += Gap + AutonomyH; // PIT: the second F4 line uses the height the missing bar frees
            }
            else
            {
                h += CompactFuelFont;
                if (!inactive) h += Gap + DividerH + DividerGap + SupportFont;
            }
            if (pace) h += Gap + SupportFont;
            h += PadV;

            float ph = h * k, pw = PanelW * k;
            float ox = edit ? EditMargin * _c : 0, oy = edit ? (BannerH + BannerGap) * _c : 0;
            _x0 = ox + PadH * k; _right = ox + (PanelW - PadH) * k;

            _d.FillRoundedRect(new RectF(ox, oy, pw, ph), PanelRadius * k, GlanceTheme.Panel(_o.BackgroundOpacity));

            var pal = GlanceTheme.For(full ? v.Decision : DecisionKind.None);
            float top = oy + PadV * k;
            if (full)
            {
                DecisionBlock(in v, in pal, top);
                top += BlockH * k;
                if (bar)
                {
                    top += Gap * k;
                    DeltaBar(in v, top);
                    top += BarH * k;
                }
                top += Gap * k;
                Support(in v, in pal, top);
                top += (SupportFont + Gap) * k;
                Divider(top);
                top += (DividerH + DividerGap) * k;
                Autonomy(in v, top);
                top += AutonomyH * k;
                if (twoLine) top += (Gap + AutonomyH) * k;
            }
            else
            {
                CompactFuel(in v, top);
                top += CompactFuelFont * k;
                if (!inactive)
                {
                    top += Gap * k;
                    Divider(top);
                    top += (DividerH + DividerGap) * k;
                    Support(in v, in pal, top);
                    top += SupportFont * k;
                }
            }
            if (pace)
            {
                top += Gap * k;
                PaceLine(in v, top);
            }

            float bottom = Pills(in v, ox + pw, oy + ph);
            float width = 2 * ox + pw;
            if (edit)
                bottom = EditChrome(ox, oy, pw, bottom - oy, bottom, ref width);
            _d.Size = new SizeF(width, bottom);
        }

        // ---- F1 ----

        readonly void DecisionBlock(in GlanceView v, in GlanceTheme.DecisionPalette pal, float top)
        {
            float k = _k, bh = BlockH * k, cy = top + bh / 2;
            // the block tint is the state colour itself (level 1, peripheral vision), not content: as the v2.1 ALVO row
            _d.FillRoundedRect(new RectF(_x0, top, InnerW * k, bh), BlockRadius * k, pal.BlockBg);
            float x = _x0 + BlockPadH * k, xr = _right - BlockPadH * k;

            string word = GlanceTheme.Word(v.Decision);
            if (word.Length > 0)
            {
                float sw = W(word, FontRole.Badge, 800, SeloFont, SeloSpacing) + 2 * SeloPadH * k, sh = (SeloFont + 2 * SeloPadV) * k;
                float sy = cy - sh / 2;
                _d.FillRoundedRect(new RectF(x, sy, sw, sh), SeloRadius * k, pal.SeloBg.WithOpacity(_op));
                T(word, FontRole.Badge, 800, SeloFont, SeloSpacing, false, pal.SeloFg, x + SeloPadH * k, sy + SeloPadV * k + Asc(FontRole.Badge, SeloFont));
                x += sw + BlockGap * k;
            }

            // PRD v3 §5.1: without a value the block shows only the word and "—"
            bool known = !float.IsNaN(v.DecisionValue);
            string label = known ? GlanceTheme.DecisionLabel(v.Decision) : "";
            if (label.Length > 0)
                x += T(label, FontRole.Label, 600, DecisionLabelFont, DecisionLabelSpacing, false, pal.Ink, x,
                    Centred(FontRole.Label, DecisionLabelFont, cy)) + BlockGap * k;

            Span<char> buf = stackalloc char[24];
            int n = NumberFormat.Fixed(v.DecisionValue, v.Decision == DecisionKind.Economize ? 2 : 1, buf);
            float nb = Centred(FontRole.ValueBold, NumberFont, cy);
            float wn = T(buf[..n], FontRole.ValueBold, 700, NumberFont, 0, false, GlanceTheme.Text, x, nb);
            if (!known) return;
            if (v.Decision is DecisionKind.Ok or DecisionKind.Pit)
                T("L", FontRole.Body, 400, UnitFont, 0, false, pal.Ink, x + wn + UnitGap * k, nb);
            else if (v.Decision == DecisionKind.Economize)
            {
                n = !float.IsNaN(v.ComplementSec) ? NumberFormat.SignedSeconds(v.ComplementSec, buf)
                    : v.ComplementPct != 0 ? NumberFormat.Percent(v.ComplementPct, buf) : 0;
                if (n > 0)
                {
                    float wc = W(buf[..n], FontRole.Value, 600, ComplementFont, 0);
                    T(buf[..n], FontRole.Value, 600, ComplementFont, 0, false, GlanceTheme.Complement, xr - wc, Centred(FontRole.Value, ComplementFont, cy));
                }
            }
        }

        // ---- F2 ----

        readonly void DeltaBar(in GlanceView v, float top)
        {
            float k = _k, half = (InnerW - 2 * BarInset) / 2 * k, bx = _x0 + BarInset * k, cx = bx + half;
            float ry = top + (BarH - RailH) / 2 * k, rh = RailH * k;
            // the inner rounded ends sit under the target line, so two plain rounded halves read as one rail
            _d.FillRoundedRect(new RectF(bx, ry, half, rh), rh / 2, GlanceTheme.BarBlue.WithOpacity(_op));
            _d.FillRoundedRect(new RectF(cx, ry, half, rh), rh / 2, GlanceTheme.BarRed.WithOpacity(_op));
            _d.FillRoundedRect(new RectF(cx - TargetW / 2 * k, top, TargetW * k, BarH * k), 0, GlanceTheme.BarTarget.WithOpacity(_op));

            int q = Math.Max(1, (int)v.BarSteps);
            if (v.BarGhostQ != GlanceView.BarAbsent)
            {
                float gx = cx + Math.Clamp(v.BarGhostQ, -q, q) / (float)q * half;
                _d.FillRoundedRect(new RectF(gx - GhostW / 2 * k, top + (BarH - GhostH) / 2 * k, GhostW * k, GhostH * k), 0,
                    GlanceTheme.BarGhost.WithOpacity(_op));
            }
            if (v.BarMarkerQ != GlanceView.BarAbsent)
            {
                int mq = Math.Clamp(v.BarMarkerQ, -q, q);
                // PRD v3 §5.2: the colour follows the instantaneous side, no hysteresis; on the target counts as blue
                var color = (mq <= 0 ? GlanceTheme.MarkerBlue : GlanceTheme.MarkerRed).WithOpacity(_op);
                float my = top + (BarH - MarkerH) / 2 * k, mh = MarkerH * k;
                if (mq == q || mq == -q)
                {
                    // saturated: a triangle against the end of the rail, pointing outwards (beyond the scale)
                    float tw = TriangleW * k;
                    _d.FillTriangle(new RectF(mq > 0 ? bx + 2 * half - tw : bx, my, tw, mh), mq > 0, color);
                }
                else
                {
                    float mx = cx + mq / (float)q * half;
                    _d.FillRoundedRect(new RectF(mx - MarkerW / 2 * k, my, MarkerW * k, mh), MarkerRadius * k, color);
                }
            }
        }

        // ---- F3 and the pace line ----

        readonly void Support(in GlanceView v, in GlanceTheme.DecisionPalette pal, float top)
        {
            float k = _k, bl = top + Asc(FontRole.Value, SupportFont), x = _x0 + RowInset * k;
            Span<char> buf = stackalloc char[24];
            int n = NumberFormat.Fixed(v.LapProj, 2, buf);
            x = Group("VOLTA", buf[..n], 600, false, pal.Volta, x, bl) + SupportGroupGap * k;
            n = NumberFormat.Fixed(v.LapLast, 2, buf);
            if (v.LapLastInvalid && !float.IsNaN(v.LapLast) && n < buf.Length) buf[n++] = '*';
            x = Group("ÚLT", buf[..n], 500, false, pal.Last, x, bl);
            if (v.ShowAvg)
            {
                n = NumberFormat.Fixed(v.LapAvg, 2, buf);
                Group("MÉD", buf[..n], 500, v.LapAvgFromBaseline, GlanceTheme.Secondary, x + SupportGroupGap * k, bl);
            }
        }

        readonly void PaceLine(in GlanceView v, float top)
        {
            float k = _k, bl = top + Asc(FontRole.Value, SupportFont), x = _x0 + RowInset * k;
            Span<char> buf = stackalloc char[24];
            int n = Seconds(v.LastLapTimeS, buf);
            x = Group("ÚLT", buf[..n], 500, false, GlanceTheme.Secondary, x, bl) + SupportGroupGap * k;
            n = Seconds(v.MeanLapTimeS, buf);
            x = Group("MÉD", buf[..n], 500, false, GlanceTheme.Secondary, x, bl);
            if (!float.IsNaN(v.BaselineLapTimeS))
            {
                n = NumberFormat.Fixed(v.BaselineLapTimeS, 2, buf);
                Group("BASE", buf[..n], 500, false, GlanceTheme.Secondary, x + SupportGroupGap * k, bl);
            }
        }

        static int Seconds(float s, Span<char> buf)
        {
            int n = NumberFormat.Fixed(s, 2, buf);
            return float.IsNaN(s) ? n : Cat(buf, n, " s");
        }

        /// <summary>A small caps label, then its value 5 px after; returns the x after the value.</summary>
        readonly float Group(ReadOnlySpan<char> label, ReadOnlySpan<char> value, int weight, bool italic, Rgba color, float x, float bl)
        {
            x += T(label, FontRole.Label, 600, SupportLabelFont, SupportLabelSpacing, false, GlanceTheme.Label, x, bl) + SupportLabelGap * _k;
            return x + T(value, FontRole.Value, weight, SupportFont, 0, italic, color, x, bl);
        }

        readonly void Divider(float top) =>
            _d.FillRoundedRect(new RectF(_x0, top, InnerW * _k, DividerH * _k), 0, GlanceTheme.Divider.WithOpacity(_op));

        // ---- F4 ----

        /// <summary>
        /// One line: <c>74.0 L · dá 31 v · faltam 32 … ALVO 2.30</c>. PIT (screen 11) has no bar, and its row does not fit
        /// one line at full size, so it takes two: <c>74.0 L · dá 31 v … alvo 2.16 · MÍN 2.23</c>, then <c>faltam 34</c>.
        /// </summary>
        readonly void Autonomy(in GlanceView v, float top)
        {
            float k = _k, bl = top + Asc(FontRole.ValueBold, TargetFont);
            float xl = _x0 + RowInset * k, xr = _right - RowInset * k;
            bool target = v.TargetLabel != TargetLabel.None, pit = v.TargetLabel == TargetLabel.PitMin;

            // left: COMB, then " L · dá N v · faltam N" (or "stint N"); in PIT the laps left go to the second line
            Span<char> fuel = stackalloc char[16], rest = stackalloc char[40], value = stackalloc char[16], grey = stackalloc char[24];
            int nf = NumberFormat.Fixed(v.Fuel, 1, fuel);
            int nr = Cat(rest, 0, " L · dá ");
            nr += NumberFormat.Count(v.LapsPossible, rest[nr..]);
            nr = Cat(rest, nr, " v");
            int line2 = nr;
            nr = Cat(rest, nr, pit ? "" : " · ");
            nr = Cat(rest, nr, v.IsStint ? "stint " : "faltam ");
            nr += NumberFormat.Count(v.LapsRemaining, rest[nr..]);
            int nl = pit ? line2 : nr;

            // right: [alvo A ·] LABEL value. The "~" marks the estimated target (PRD v3 §5.4); in PIT that is the grey
            // "alvo" number, not MÍN.
            int nv = !target ? 0 : pit ? NumberFormat.Fixed(v.FeasibleMin, 2, value) : Target(in v, value);
            int ng = 0;
            if (pit)
            {
                ng = Cat(grey, 0, "alvo ");
                ng += Target(in v, grey[ng..]);
                ng = Cat(grey, ng, " ·");
            }
            string word = GlanceTheme.TargetWord(v.TargetLabel);
            float wv = target ? W(value[..nv], FontRole.ValueBold, 700, TargetFont, 0) : 0;
            float ww = target ? TargetGap * k + W(word, FontRole.Label, 600, SupportLabelFont, SupportLabelSpacing) : 0;

            // Two sizes only: the full size whenever the current line 1 fits, else the one fallback size of the widest
            // template. The size changes only when the content crosses the fit threshold (faltam 999 → 99), never from
            // per-frame noise in the values.
            float need = W(fuel[..nf], FontRole.Value, 600, AutonomyFont, 0) + W(rest[..nl], FontRole.Value, 400, AutonomyFont, 0)
                + (target ? AutonomyMinGap * k + wv + ww : 0) + (pit ? TargetGap * k + W(grey[..ng], FontRole.Value, 400, PitTargetFont, 0) : 0);
            float f = need <= xr - xl + 0.001f * k ? 1 : AutonomyFactor(pit);

            float rx = xr;
            if (target)
            {
                rx -= wv;
                T(value[..nv], FontRole.ValueBold, 700, TargetFont, 0, false, GlanceTheme.Text, rx, bl);
                rx -= ww;
                T(word, FontRole.Label, 600, SupportLabelFont, SupportLabelSpacing, false, GlanceTheme.Label, rx, bl);
                if (pit)
                {
                    rx -= TargetGap * k + W(grey[..ng], FontRole.Value, 400, PitTargetFont * f, 0);
                    T(grey[..ng], FontRole.Value, 400, PitTargetFont * f, 0, false, GlanceTheme.Label, rx, bl);
                }
            }
            float wf = T(fuel[..nf], FontRole.Value, 600, AutonomyFont * f, 0, false, GlanceTheme.Text, xl, bl);
            T(rest[..nl], FontRole.Value, 400, AutonomyFont * f, 0, false, GlanceTheme.Secondary, xl + wf, bl, true);
            if (pit)
                T(rest[line2..nr], FontRole.Value, 400, AutonomyFont * f, 0, false, GlanceTheme.Secondary, xl, bl + (AutonomyH + Gap) * k);
        }

        /// <summary>
        /// The fallback factor for the F4 secondary runs (left text, grey "alvo") when the current content does not fit:
        /// computed against the widest template of the arrangement (V3-7: 100.0 L · dá 99 v · faltam 999 next to ~2.30),
        /// not against the values shown, so every overflowing row of a scale shares one size. The target label and value
        /// keep their size.
        /// </summary>
        readonly float AutonomyFactor(bool pit)
        {
            float k = _k;
            float fixedW = W(pit ? "2.30" : "~2.30", FontRole.ValueBold, 700, TargetFont, 0)
                + TargetGap * k + W(pit ? "MÍN" : "ALVO", FontRole.Label, 600, SupportLabelFont, SupportLabelSpacing)
                + AutonomyMinGap * k + (pit ? TargetGap * k : 0);
            float flexW = W("100.0", FontRole.Value, 600, AutonomyFont, 0)
                + W(pit ? " L · dá 99 v" : " L · dá 99 v · faltam 999", FontRole.Value, 400, AutonomyFont, 0)
                + (pit ? W("alvo ~2.30 ·", FontRole.Value, 400, PitTargetFont, 0) : 0);
            float avail = (InnerW - 2 * RowInset) * k - fixedW;
            return flexW > avail && flexW > 0 ? avail / flexW : 1;
        }

        static int Target(in GlanceView v, Span<char> dest)
        {
            int n = 0;
            if (v.IsEstimate && !float.IsNaN(v.Target) && !dest.IsEmpty) dest[n++] = '~';
            return n + NumberFormat.Fixed(v.Target, 2, dest[n..]);
        }

        // ---- compact / inactive first band ----

        readonly void CompactFuel(in GlanceView v, float top)
        {
            float k = _k, bl = top + Asc(FontRole.Value, CompactFuelFont);
            float xl = _x0 + RowInset * k, xr = _right - RowInset * k;
            Span<char> buf = stackalloc char[40];
            int n = NumberFormat.Fixed(v.Fuel, 1, buf);
            float wf = T(buf[..n], FontRole.Value, 600, CompactFuelFont, 0, false, GlanceTheme.Text, xl, bl);
            T("L", FontRole.Body, 400, CompactUnitFont, 0, false, GlanceTheme.Label, xl + wf + CompactUnitGap * k, bl);

            n = Cat(buf, 0, "dá ");
            n += NumberFormat.Count(v.LapsPossible, buf[n..]);
            n = Cat(buf, n, " v");
            if (v.MinutesPossible >= 0) // unknown minutes are left out rather than shown as "—"
            {
                n = Cat(buf, n, " · ");
                n += NumberFormat.Count(v.MinutesPossible, buf[n..]);
                n = Cat(buf, n, " min");
            }
            float w = W(buf[..n], FontRole.Value, 400, CompactAutonomyFont, 0);
            T(buf[..n], FontRole.Value, 400, CompactAutonomyFont, 0, false, GlanceTheme.Secondary, xr - w, bl);
        }

        // ---- pills (PRD v3 §4.3) ----

        /// <summary>Right-aligned under the panel, laid out from the right: ~ CAUTION, BoP≠, REC, DBG. Returns the new bottom.</summary>
        readonly float Pills(in GlanceView v, float edge, float panelBottom)
        {
            bool any = v.Pills != StatusPills.None || _o.DebugTag;
            if (!any) return panelBottom;
            float py = panelBottom + PillGap * _k;
            if ((v.Pills & StatusPills.Caution) != 0) edge = Pill("~ CAUTION", false, GlanceTheme.Text, edge, py);
            if ((v.Pills & StatusPills.BopMismatch) != 0) edge = Pill("BoP≠", false, GlanceTheme.Text, edge, py);
            if ((v.Pills & StatusPills.Rec) != 0)
            {
                Span<char> buf = stackalloc char[24];
                int n = Cat(buf, 0, "REC ");
                n += Int(v.RecValid, buf[n..]);
                n = Cat(buf, n, "/");
                n += Int(v.RecTotal, buf[n..]);
                edge = Pill(buf[..n], true, GlanceTheme.Text, edge, py);
            }
            if (_o.DebugTag) Pill("DBG", false, GlanceTheme.Label, edge, py);
            return py + PillH * _k;
        }

        /// <summary>One pill ending at <paramref name="edge"/>; returns the edge for the next pill on its left.</summary>
        readonly float Pill(ReadOnlySpan<char> text, bool dot, Rgba color, float edge, float py)
        {
            float k = _k, lead = dot ? (DotSize + DotGap) * k : 0;
            float w = PillPadH * k + lead + W(text, FontRole.Value, 600, PillFont, 0) + PillPadH * k, h = PillH * k;
            float px = edge - w;
            _d.FillRoundedRect(new RectF(px, py, w, h), PillRadius * k, GlanceTheme.Panel(_o.BackgroundOpacity));
            if (dot)
                _d.FillRoundedRect(new RectF(px + PillPadH * k, py + (h - DotSize * k) / 2, DotSize * k, DotSize * k), DotSize / 2 * k,
                    GlanceTheme.RecDot.WithOpacity(_op));
            T(text, FontRole.Value, 600, PillFont, 0, false, color, px + PillPadH * k + lead, py + PillPadV * k + Asc(FontRole.Value, PillFont));
            return px - PillGap * k;
        }

        // ---- edit chrome (copied from the v2.1 OverlayLayout; the box wraps the panel and the pills, PRD v3 §6.6) ----

        readonly float EditChrome(float ox, float oy, float pw, float boxH, float bottom, ref float width)
        {
            float c = _c;
            var white = GlanceTheme.White;
            // outline and handles
            _d.StrokeDashedRect(new RectF(ox - 3 * c, oy - 3 * c, pw + 6 * c, boxH + 6 * c), c, Rgba.From(255, 255, 255, 0.75));
            float hs = EditMargin * c;
            ReadOnlySpan<float> hx = [ox - hs, ox + pw, ox - hs, ox + pw];
            ReadOnlySpan<float> hy = [oy - hs, oy - hs, oy + boxH, oy + boxH];
            for (int i = 0; i < 4; i++)
            {
                var r = new RectF(hx[i], hy[i], hs, hs);
                // the inactive panel's dimming is content, not chrome: the handles keep the user's content opacity only
                _d.FillRect(r, white.WithOpacity(_o.ContentOpacity));
                _d.StrokeRect(r, c, GlanceTheme.Accent.WithOpacity(_o.ContentOpacity));
            }

            // banner above (does not follow the user's scale)
            float f = ChromeFont * c;
            float asc = _m.Ascent(FontRole.Label, f);
            float w1 = _m.Width("MODO EDIÇÃO", FontRole.Label, 600, f, 0.08f);
            float w2 = _m.Width("arraste para mover · puxe um canto para redimensionar", FontRole.Body, 400, f, 0);
            float bw = 18 * c + w1 + 7 * c + w2;
            _d.FillRoundedRect(new RectF(ox, 0, bw, BannerH * c), 4 * c, GlanceTheme.Accent);
            _d.Text("MODO EDIÇÃO", FontRole.Label, 600, f, 0.08f, false, white, ox + 9 * c, 5 * c + asc);
            _d.Text("arraste para mover · puxe um canto para redimensionar", FontRole.Body, 400, f, 0, false, white, ox + 9 * c + w1 + 7 * c, 5 * c + asc);
            width = Math.Max(width, ox + bw);

            // footer pills
            Span<char> info = stackalloc char[64];
            int n = Cat(info, 0, "X ");
            n += Int(_o.ScreenX, info[n..]);
            n = Cat(info, n, " · Y ");
            n += Int(_o.ScreenY, info[n..]);
            n = Cat(info, n, " · escala ");
            n += Int((int)Math.Round(_o.Scale * 100, MidpointRounding.AwayFromZero), info[n..]);
            n = Cat(info, n, "%");

            const string rest = " trava e volta ao modo click-through";
            float wk = _m.Width(_o.ToggleEditHotkey, FontRole.Value, 600, f, 0), wrest = _m.Width(rest, FontRole.Body, 400, f, 0);
            float wl = 12 * c + wk + wrest, wr = _m.Width(info[..n], FontRole.Value, 400, f, 0) + 12 * c;
            float fy = Math.Max(bottom, oy + boxH + EditMargin * c) + 6 * c, fh = f + 6 * c;
            var bg = Rgba.From(GlanceTheme.PanelR, GlanceTheme.PanelG, GlanceTheme.PanelB, 0.75);
            float fa = _m.Ascent(FontRole.Body, f);

            _d.FillRoundedRect(new RectF(ox, fy, wl, fh), 6 * c, bg);
            _d.Text(_o.ToggleEditHotkey, FontRole.Value, 600, f, 0, false, GlanceTheme.ChromeText, ox + 6 * c, fy + 3 * c + fa);
            _d.Text(rest, FontRole.Body, 400, f, 0, false, GlanceTheme.ChromeText, ox + 6 * c + wk, fy + 3 * c + fa);
            float ry = fy;
            if (wl + 6 * c + wr > pw) ry = fy + fh + 6 * c; // does not fit beside it: stack
            float rx = Math.Max(ox, ox + pw - wr);
            _d.FillRoundedRect(new RectF(rx, ry, wr, fh), 6 * c, bg);
            _d.Text(info[..n], FontRole.Value, 400, f, 0, false, GlanceTheme.ChromeText, rx + 6 * c, ry + 3 * c + fa);
            width = Math.Max(width, Math.Max(ox + wl, rx + wr));
            return ry + fh;
        }
    }
}
