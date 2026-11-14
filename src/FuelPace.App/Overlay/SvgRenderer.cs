using System.Globalization;
using System.Text;

namespace FuelPace.App.Overlay;

/// <summary>Renders a <see cref="DrawList"/> as SVG for the review previews. Fonts are referenced by family name only.</summary>
public static class SvgRenderer
{
    const string InterFamily = "Inter, system-ui, sans-serif";
    const string MonoFamily = "'JetBrains Mono', monospace";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(DrawList list) => Render(list, 0, false);

    /// <summary>
    /// <paramref name="padding"/> px around the overlay; with <paramref name="scene"/> a stand-in for the game picture
    /// (sky, ground, track, dashboard) is drawn behind it, as in the mocks, so the translucent panel can be judged.
    /// </summary>
    public static string Render(DrawList list, int padding, bool scene)
    {
        float w = list.Size.W + 2 * padding, h = list.Size.H + 2 * padding;
        var sb = new StringBuilder(4096);
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\" viewBox=\"0 0 {F(w)} {F(h)}\">\n");
        if (scene)
        {
            float sky = h * 0.43f, bar = h * 0.82f;
            sb.Append(Inv, $"<rect width=\"{F(w)}\" height=\"{F(h)}\" fill=\"#1C2026\"/>\n");
            sb.Append(Inv, $"<rect width=\"{F(w)}\" height=\"{F(sky)}\" fill=\"#3A4552\"/>\n");
            sb.Append(Inv, $"<rect y=\"{F(sky)}\" width=\"{F(w)}\" height=\"{F(h - sky)}\" fill=\"#262B31\"/>\n");
            sb.Append(Inv, $"<polygon points=\"{F(w * 0.46f)},{F(sky)} {F(w * 0.54f)},{F(sky)} {F(w)},{F(h)} 0,{F(h)}\" fill=\"#3B4048\"/>\n");
            sb.Append(Inv, $"<rect y=\"{F(bar)}\" width=\"{F(w)}\" height=\"{F(h - bar)}\" fill=\"#0E1013\"/>\n");
        }
        sb.Append(Inv, $"<g transform=\"translate({padding} {padding})\">\n");
        bool open = false;
        foreach (var c in list.Commands)
        {
            if (open && !(c.Kind == CommandKind.Text && c.Continuation)) { sb.Append("</text>\n"); open = false; }
            switch (c.Kind)
            {
                case CommandKind.FillRoundedRect:
                    sb.Append(Inv, $"<rect x=\"{F(c.Rect.X)}\" y=\"{F(c.Rect.Y)}\" width=\"{F(c.Rect.W)}\" height=\"{F(c.Rect.H)}\" rx=\"{F(c.Radius)}\"");
                    Paint(sb, "fill", c.Color); sb.Append("/>\n");
                    break;
                case CommandKind.FillRect:
                    sb.Append(Inv, $"<rect x=\"{F(c.Rect.X)}\" y=\"{F(c.Rect.Y)}\" width=\"{F(c.Rect.W)}\" height=\"{F(c.Rect.H)}\"");
                    Paint(sb, "fill", c.Color); sb.Append("/>\n");
                    break;
                case CommandKind.StrokeRect:
                case CommandKind.StrokeDashedRect:
                    // SVG strokes are centered on the edge; inset by half the width so the line stays inside the rect.
                    float hw = c.Radius / 2;
                    sb.Append(Inv, $"<rect x=\"{F(c.Rect.X + hw)}\" y=\"{F(c.Rect.Y + hw)}\" width=\"{F(c.Rect.W - c.Radius)}\" height=\"{F(c.Rect.H - c.Radius)}\" fill=\"none\" stroke-width=\"{F(c.Radius)}\"");
                    if (c.Kind == CommandKind.StrokeDashedRect) sb.Append(" stroke-dasharray=\"4 3\"");
                    Paint(sb, "stroke", c.Color); sb.Append("/>\n");
                    break;
                case CommandKind.FillTriangle:
                {
                    float x0 = c.Rect.X, x1 = c.Rect.X + c.Rect.W, y0 = c.Rect.Y, y1 = c.Rect.Y + c.Rect.H, ym = y0 + c.Rect.H / 2;
                    if (c.Radius > 0) sb.Append(Inv, $"<polygon points=\"{F(x0)},{F(y0)} {F(x1)},{F(ym)} {F(x0)},{F(y1)}\"");
                    else sb.Append(Inv, $"<polygon points=\"{F(x1)},{F(y0)} {F(x0)},{F(ym)} {F(x1)},{F(y1)}\"");
                    Paint(sb, "fill", c.Color); sb.Append("/>\n");
                    break;
                }
                case CommandKind.Text:
                    bool mono = c.Role is FontRole.Value or FontRole.ValueBold;
                    // A continuation flows after the previous run inside the same <text>, so the browser's own font
                    // metrics decide the spacing instead of the estimate used for layout.
                    sb.Append(c.Continuation && open ? "<tspan" : "<text");
                    if (!(c.Continuation && open))
                        sb.Append(Inv, $" x=\"{F(c.Rect.X)}\" y=\"{F(c.Rect.Y)}\" xml:space=\"preserve\"");
                    sb.Append(Inv, $" font-family=\"{(mono ? MonoFamily : InterFamily)}\" font-size=\"{F(c.SizePx)}\" font-weight=\"{c.Weight}\"");
                    if (c.Italic) sb.Append(" font-style=\"italic\"");
                    if (c.LetterSpacingEm != 0) sb.Append(Inv, $" letter-spacing=\"{F(c.LetterSpacingEm * c.SizePx)}\"");
                    Paint(sb, "fill", c.Color);
                    sb.Append('>');
                    Escape(sb, list.TextOf(in c));
                    if (c.Continuation && open) sb.Append("</tspan>");
                    else open = true;
                    break;
            }
        }
        if (open) sb.Append("</text>\n");
        sb.Append("</g>\n</svg>\n");
        return sb.ToString();
    }

    static string F(float v) => v.ToString("0.##", Inv);

    static void Paint(StringBuilder sb, string attr, Rgba c)
    {
        sb.Append(Inv, $" {attr}=\"#{c.R:X2}{c.G:X2}{c.B:X2}\"");
        if (c.A != 255) sb.Append(Inv, $" {attr}-opacity=\"{(c.A / 255.0).ToString("0.###", Inv)}\"");
    }

    static void Escape(StringBuilder sb, ReadOnlySpan<char> t)
    {
        foreach (char ch in t)
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(ch); break;
            }
    }
}
