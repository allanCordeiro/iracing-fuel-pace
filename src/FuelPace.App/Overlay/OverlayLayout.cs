using FuelPace.Core.Strategy;

namespace FuelPace.App.Overlay;

/// <param name="Scale">The user's scale (0.5–2.5), shown in the edit-mode footer.</param>
/// <param name="DpiScale">The monitor's dpi / 96: the panel is drawn at Scale × DpiScale and the edit chrome at DpiScale,
/// so the list is in physical pixels.</param>
public sealed record OverlayViewOptions(
    double Scale, double BackgroundOpacity, double ContentOpacity, bool ShowPaceLine, bool ShowInsightLine,
    bool EditMode, bool DebugTag, string ToggleEditHotkey, int ScreenX, int ScreenY, double DpiScale = 1.0);

/// <summary>
/// Lays the overlay out as a <see cref="DrawList"/>, matching the approved mocks at 1x (width 300 px, line height 1.0).
/// Everything inside the panel scales with <see cref="OverlayViewOptions.Scale"/> × <see cref="OverlayViewOptions.DpiScale"/>;
/// the edit-mode chrome (banner, footer, outline, handles) does not follow the user's scale, only the DPI. Zero allocation
/// once the list has warmed up.
/// </summary>
public static class OverlayLayout
{
    public static void Build(in OverlayState s, ReadOnlySpan<char> carShort, ReadOnlySpan<char> trackShort,
        OverlayViewOptions o, ITextMeasurer m, DrawList dest)
    {
        dest.Clear();
        new Builder(o, m, dest).Run(in s, carShort, trackShort);
    }

    /// <summary>Where the panel's top-left corner sits inside the list (physical px): edit mode adds chrome above and to the
    /// left of it (sized by <paramref name="dpiScale"/> only), so the window must start that far before the panel.</summary>
    public static SizeF PanelOffset(bool editMode, double dpiScale = 1.0) =>
        editMode ? new SizeF(EditMargin * (float)dpiScale, (BannerH + BannerGap) * (float)dpiScale) : default;

    // 1x metrics (px)
    const float PanelW = 300, PadV = 9, PadH = 11, Gap = 6, RowGap = 6;
    const float EditMargin = 7, BannerH = 21, BannerGap = 10, ChromeFont = 11;

    struct Builder
    {
        readonly OverlayViewOptions _o;
        readonly ITextMeasurer _m;
        readonly DrawList _d;
        readonly float _k, _c; // panel factor (Scale × DPI), chrome factor (DPI)
        double _op;

        public Builder(OverlayViewOptions o, ITextMeasurer m, DrawList d)
        {
            _o = o; _m = m; _d = d; _c = (float)o.DpiScale; _k = (float)o.Scale * _c; _op = o.ContentOpacity;
        }

        float W(ReadOnlySpan<char> t, FontRole r, float size, float ls) => _m.Width(t, r, size * _k, ls);

        float T(ReadOnlySpan<char> t, FontRole r, int weight, float size, float ls, bool italic, Rgba c, float x, float baseline, bool cont = false)
        {
            _d.Text(t, r, weight, size * _k, ls, italic, c.WithOpacity(_op), x, baseline, cont);
            return W(t, r, size, ls);
        }

        float InsightWidth(ReadOnlySpan<char> t, int len, int boldLen)
        {
            int b = Math.Min(boldLen, len);
            return W(t[..b], FontRole.BodyBold, 11, 0) + W(t[b..len], FontRole.Body, 11, 0);
        }

        float Asc(FontRole r, float size) => _m.Ascent(r, size * _k);

        static int Cat(Span<char> dest, int pos, ReadOnlySpan<char> s)
        {
            int n = Math.Min(s.Length, dest.Length - pos);
            s[..n].CopyTo(dest[pos..]);
            return pos + n;
        }

        static int Int(int v, Span<char> dest) => v.TryFormat(dest, out int n, default, System.Globalization.CultureInfo.InvariantCulture) ? n : 0;

        public void Run(in OverlayState s, ReadOnlySpan<char> carShort, ReadOnlySpan<char> trackShort)
        {
            float k = _k;
            bool edit = _o.EditMode, full = s.State != SignalState.Inactive;
            if (!full) _op *= 0.5;
            var pal = OverlayTheme.For(s.State);

            Span<char> insight = stackalloc char[160];
            int insightLen = 0, boldLen = 0;
            if (full && _o.ShowInsightLine)
                insightLen = OverlayText.Insight(in s.Insight, carShort, trackShort, insight[..128], out boldLen);
            bool paceLine = full && _o.ShowPaceLine;

            // Panel height (1x): rows have line height 1.0.
            bool notFeasible = s.State == SignalState.NotFeasible;
            float row3Content = notFeasible ? 12 + 1 + 8.5f : 18;
            float h = PadV + 21 + PadV;
            if (full) h += RowGap + 16 + RowGap + (10 + row3Content);
            if (paceLine) h += RowGap + 10;
            if (insightLen > 0) h += RowGap + 11;
            float ph = h * k, pw = PanelW * k;
            float ox = edit ? EditMargin * _c : 0, oy = edit ? (BannerH + BannerGap) * _c : 0;
            float x0 = ox + PadH * k, right = ox + (PanelW - PadH) * k;

            _d.FillRoundedRect(new RectF(ox, oy, pw, ph), 6 * k, OverlayTheme.Panel(_o.BackgroundOpacity));

            Span<char> buf = stackalloc char[96];

            // Row 1: fuel
            float top = oy + PadV * k;
            float bl = top + Asc(FontRole.Value, 21);
            T("COMB", FontRole.Label, 600, 9, 0.1f, false, OverlayTheme.Label, x0, bl);
            int n = OverlayText.Liters(s.FuelL, buf);
            float vx = x0 + 54 * k;
            float wv = T(buf[..n], FontRole.Value, 600, 21, 0, false, OverlayTheme.Text, vx, bl);
            T("L", FontRole.Body, 400, 10, 0, false, OverlayTheme.Label, vx + wv + 8 * k, bl);
            int p = OverlayText.Laps(s.AutonomyLaps, buf);
            p = Cat(buf, p, " v · ");
            p += OverlayText.Minutes(s.AutonomyMin, buf[p..]);
            p = Cat(buf, p, " min");
            float wr = W(buf[..p], FontRole.Value, 12, 0);
            T(buf[..p], FontRole.Value, 400, 12, 0, false, OverlayTheme.Secondary, right - wr, bl);
            top += 21 * k;

            if (full)
            {
                // Row 2: VOLTA / ÚLT / MÉD
                top += RowGap * k;
                bl = top + Asc(FontRole.Value, 16);
                float x = x0;
                T("VOLTA", FontRole.Label, 600, 9, 0.1f, false, OverlayTheme.Label, x, bl);
                n = OverlayText.PerLap(s.LapProjectionL, buf);
                x += 54 * k;
                x += T(buf[..n], FontRole.ValueBold, 700, 16, 0, false, pal.Volta, x, bl) + 20 * k;

                x += T("ÚLT", FontRole.Label, 600, 9, 0.1f, false, OverlayTheme.Label, x, bl) + 6 * k;
                n = OverlayText.PerLap(s.LastLapL, buf);
                if (s.LastLapInvalid && n < buf.Length) buf[n++] = '*';
                x += T(buf[..n], FontRole.Value, 500, 16, 0, false, pal.Last, x, bl) + 20 * k;

                x += T("MÉD", FontRole.Label, 600, 9, 0.1f, false, OverlayTheme.Label, x, bl) + 6 * k;
                n = OverlayText.PerLap(s.MeanL, buf);
                x += T(buf[..n], FontRole.Value, 500, 16, 0, s.MeanFromBaseline, OverlayTheme.Text, x, bl) + 6 * k;
                T("L/v", FontRole.Body, 400, 9, 0, false, OverlayTheme.Label, x, bl);
                top += 16 * k;

                // Row 3: ALVO
                top += RowGap * k;
                float rowH = (10 + row3Content) * k;
                _d.FillRoundedRect(new RectF(ox + 7 * k, top, 286 * k, rowH), 4 * k, pal.RowBg);
                float ct = top + 5 * k, ch = row3Content * k;
                float il = ox + 14 * k, ir = ox + 286 * k;

                float wl;
                if (notFeasible)
                {
                    float tb = ct + Asc(FontRole.Badge, 12);
                    float w1 = T("NÃO FACTÍVEL", FontRole.Badge, 800, 12, 0.06f, false, pal.Ink, il, tb);
                    p = Cat(buf, 0, "alvo ");
                    p += OverlayText.PerLap(s.TargetL, buf[p..]);
                    p = Cat(buf, p, " · mín possível ");
                    p += OverlayText.PerLap(s.FeasibleMinL, buf[p..]);
                    float w2 = T(buf[..p], FontRole.Value, 400, 8.5f, 0, false, OverlayTheme.Subtitle, il, ct + 13 * k + Asc(FontRole.Value, 8.5f));
                    wl = Math.Max(w1, w2);
                }
                else
                {
                    float tb = ct + Asc(FontRole.Value, 18);
                    T("ALVO", FontRole.Label, 600, 9, 0.1f, false, pal.Ink, il, tb);
                    p = 0;
                    if (s.TargetIsEstimate && !double.IsNaN(s.TargetL)) buf[p++] = '~';
                    p += OverlayText.PerLap(s.TargetL, buf[p..]);
                    var vc = double.IsNaN(s.TargetL) ? OverlayTheme.Label : OverlayTheme.Text;
                    float tx = il + 51 * k;
                    float w1 = T(buf[..p], FontRole.ValueBold, 700, 18, 0, false, vc, tx, tb);
                    float w2 = T("L/v", FontRole.Body, 400, 9, 0, false, pal.Ink, tx + w1 + 8 * k, tb);
                    wl = 51 * k + w1 + 8 * k + w2;
                }

                // centre text
                if (s.IsStintMode && s.State is SignalState.Blue or SignalState.Red or SignalState.NotFeasible)
                {
                    p = Cat(buf, 0, "STINT ");
                    p += OverlayText.Laps(s.LapsRemaining, buf[p..]);
                    p = Cat(buf, p, " v");
                }
                else if (s.State is SignalState.NoBaseline or SignalState.NoTarget)
                    p = Cat(buf, 0, s.IsPractice ? "treino · sem limite" : "sem limite");
                else
                {
                    p = Cat(buf, 0, "faltam ");
                    p += OverlayText.Laps(s.LapsRemaining, buf[p..]);
                    p = Cat(buf, p, " v");
                }
                float wc = W(buf[..p], FontRole.Value, 11, 0);

                // badge
                float wb = 0;
                if (pal.BadgeText is { } badge)
                {
                    float wt = W(badge, FontRole.Badge, 8, 0.08f);
                    wb = wt + 12 * k;
                    float bx = ir - wb, by = ct + (ch - 14 * k) / 2;
                    _d.FillRoundedRect(new RectF(bx, by, wb, 14 * k), 3 * k, pal.BadgeBg.WithOpacity(_op));
                    T(badge, FontRole.Badge, (s.State == SignalState.NotFeasible ? 800 : 700), 8, 0.08f, false, pal.BadgeFg, bx + 6 * k, by + 3 * k + Asc(FontRole.Badge, 8));
                }
                // space-between: equal gaps (the mock's NÃO FACTÍVEL row leaves only about 4 px each side at 1x)
                float cx = wb > 0 ? il + wl + Math.Max(0, (ir - il - wl - wc - wb) / 2) : Math.Max(il + wl, ir - wc);
                T(buf[..p], FontRole.Value, 400, 11, 0, false, pal.Center, cx, ct + (ch - 11 * k) / 2 + Asc(FontRole.Value, 11));
                top += rowH;

                if (paceLine)
                {
                    top += RowGap * k;
                    p = Cat(buf, 0, "ÚLT ");
                    p += OverlayText.Seconds(s.LastLapTimeS, buf[p..]);
                    p = Cat(buf, p, " s · MÉD ");
                    p += OverlayText.Seconds(s.MeanLapTimeS, buf[p..]);
                    if (!double.IsNaN(s.BaselineLapTimeS))
                    {
                        p = Cat(buf, p, " · base ");
                        p += OverlayText.Seconds(s.BaselineLapTimeS, buf[p..]);
                    }
                    T(buf[..p], FontRole.Value, 400, 10, 0, false, OverlayTheme.Secondary, x0, top + Asc(FontRole.Value, 10));
                    top += 10 * k;
                }

                if (insightLen > 0)
                {
                    top += RowGap * k;
                    bl = top + Asc(FontRole.Body, 11);
                    // keep the line inside the panel: cut and end with an ellipsis (car/track names can be long)
                    float avail = right - x0;
                    if (InsightWidth(insight, insightLen, boldLen) > avail)
                    {
                        int len = insightLen;
                        while (len > 1 && InsightWidth(insight, len, boldLen) + W("…", FontRole.Body, 11, 0) > avail) len--;
                        insight[len] = '…';
                        insightLen = len + 1;
                        boldLen = Math.Min(boldLen, len);
                    }
                    float wbold = 0;
                    if (boldLen > 0)
                        wbold = T(insight[..boldLen], FontRole.BodyBold, 700, 11, 0, false, OverlayTheme.Insight, x0, bl);
                    if (insightLen > boldLen)
                        T(insight[boldLen..insightLen], FontRole.Body, 400, 11, 0, false, OverlayTheme.Insight, x0 + wbold, bl, boldLen > 0);
                }
            }

            // pills under the panel
            float bottom = oy + ph, width = 2 * ox + pw;
            if (s.Recording || _o.DebugTag)
            {
                float py = bottom + 4 * k, pillH = 17 * k, tb = py + 4 * k + Asc(FontRole.Value, 9);
                var bg = OverlayTheme.Panel(_o.BackgroundOpacity);
                float edge = ox + pw;
                if (s.Recording)
                {
                    p = Int(s.RecValid, buf);
                    buf[p++] = '/';
                    p += Int(s.RecTotal, buf[p..]);
                    p = Cat(buf, p, " válidas");
                    float wRec = W("REC", FontRole.Badge, 8, 0.1f), wCnt = W(buf[..p], FontRole.Value, 9, 0);
                    float pwid = 14 * k + 6 * k + 5 * k + wRec + 5 * k + wCnt;
                    float px = edge - pwid;
                    _d.FillRoundedRect(new RectF(px, py, pwid, pillH), 4 * k, bg);
                    _d.FillRoundedRect(new RectF(px + 7 * k, py + (pillH - 6 * k) / 2, 6 * k, 6 * k), 3 * k, OverlayTheme.RecDot.WithOpacity(_op));
                    float tx = px + 7 * k + 11 * k;
                    T("REC", FontRole.Badge, 700, 8, 0.1f, false, OverlayTheme.Text, tx, tb);
                    T(buf[..p], FontRole.Value, 400, 9, 0, false, OverlayTheme.Secondary, tx + wRec + 5 * k, tb);
                    edge = px - 4 * k;
                }
                if (_o.DebugTag)
                {
                    float wd = W("DBG", FontRole.Badge, 8, 0), pwid = 14 * k + wd;
                    float px = edge - pwid;
                    _d.FillRoundedRect(new RectF(px, py, pwid, pillH), 4 * k, bg);
                    T("DBG", FontRole.Badge, 700, 8, 0, false, OverlayTheme.Label, px + 7 * k, tb);
                }
                bottom = py + pillH;
            }

            if (edit)
                bottom = EditChrome(ox, oy, pw, ph, bottom, ref width);

            _d.Size = new SizeF(width, bottom);
        }

        float EditChrome(float ox, float oy, float pw, float ph, float bottom, ref float width)
        {
            float c = _c;
            var white = OverlayTheme.White;
            // outline and handles
            _d.StrokeDashedRect(new RectF(ox - 3 * c, oy - 3 * c, pw + 6 * c, ph + 6 * c), c, Rgba.From(255, 255, 255, 0.75));
            float hs = EditMargin * c;
            ReadOnlySpan<float> hx = [ox - hs, ox + pw, ox - hs, ox + pw];
            ReadOnlySpan<float> hy = [oy - hs, oy - hs, oy + ph, oy + ph];
            for (int i = 0; i < 4; i++)
            {
                var r = new RectF(hx[i], hy[i], hs, hs);
                _d.FillRect(r, white.WithOpacity(_op));
                _d.StrokeRect(r, c, OverlayTheme.Accent.WithOpacity(_op));
            }

            // banner above (does not follow the user's scale)
            float f = ChromeFont * c;
            float asc = _m.Ascent(FontRole.Label, f);
            float w1 = _m.Width("MODO EDIÇÃO", FontRole.Label, f, 0.08f);
            float w2 = _m.Width("arraste para mover · puxe um canto para redimensionar", FontRole.Body, f, 0);
            float bw = 18 * c + w1 + 7 * c + w2;
            _d.FillRoundedRect(new RectF(ox, 0, bw, BannerH * c), 4 * c, OverlayTheme.Accent);
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
            float wk = _m.Width(_o.ToggleEditHotkey, FontRole.Value, f, 0), wrest = _m.Width(rest, FontRole.Body, f, 0);
            float wl = 12 * c + wk + wrest, wr = _m.Width(info[..n], FontRole.Value, f, 0) + 12 * c;
            float fy = Math.Max(bottom, oy + ph + EditMargin * c) + 6 * c, fh = f + 6 * c;
            var bg = Rgba.From(OverlayTheme.PanelR, OverlayTheme.PanelG, OverlayTheme.PanelB, 0.75);
            float fa = _m.Ascent(FontRole.Body, f);

            _d.FillRoundedRect(new RectF(ox, fy, wl, fh), 6 * c, bg);
            _d.Text(_o.ToggleEditHotkey, FontRole.Value, 600, f, 0, false, OverlayTheme.Insight, ox + 6 * c, fy + 3 * c + fa);
            _d.Text(rest, FontRole.Body, 400, f, 0, false, OverlayTheme.Insight, ox + 6 * c + wk, fy + 3 * c + fa);
            float ry = fy;
            if (wl + 6 * c + wr > pw) ry = fy + fh + 6 * c; // does not fit beside it: stack
            float rx = Math.Max(ox, ox + pw - wr);
            _d.FillRoundedRect(new RectF(rx, ry, wr, fh), 6 * c, bg);
            _d.Text(info[..n], FontRole.Value, 400, f, 0, false, OverlayTheme.Insight, rx + 6 * c, ry + 3 * c + fa);
            width = Math.Max(width, Math.Max(ox + wl, rx + wr));
            return ry + fh;
        }
    }
}
