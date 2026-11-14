using System.Windows;
using System.Windows.Media;
using FuelPace.App.Overlay;

namespace FuelPace.Settings;

/// <summary>
/// The overlay preview of the Settings window ("PRÉVIA · 100%", mock 8): draws a <see cref="DrawList"/> with a
/// <see cref="DrawingContext"/> as the Agent's Direct2D renderer does. Colors are straight alpha; strokes stay inside their
/// rect; text uses each command's role, weight, size, letter spacing and italic (<see cref="EmbeddedFonts"/>), a
/// <see cref="DrawCommand.Continuation"/> run starts where the previous run's advance ends, on the same baseline, and
/// baselines snap to whole pixels. Sized to <see cref="DrawList.Size"/> (the list is in DIPs at DpiScale 1).
/// </summary>
public sealed class WpfDrawListRenderer : FrameworkElement
{
    public static readonly DependencyProperty ListProperty = DependencyProperty.Register(nameof(List), typeof(DrawList),
        typeof(WpfDrawListRenderer), new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public DrawList? List
    {
        get => (DrawList?)GetValue(ListProperty);
        set => SetValue(ListProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => List is { } l ? new Size(l.Size.W, l.Size.H) : default;

    protected override void OnRender(DrawingContext dc)
    {
        if (List is { } l) Draw(dc, l, EmbeddedFonts.Shared, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    public static void Draw(DrawingContext dc, DrawList list, EmbeddedFonts fonts, double pixelsPerDip)
    {
        double prevX = 0, prevAdvance = 0;
        bool prevText = false;
        foreach (ref readonly var c in list.Commands)
        {
            var r = c.Rect;
            switch (c.Kind)
            {
                case CommandKind.FillRoundedRect:
                    dc.DrawRoundedRectangle(Brush(c.Color), null, new Rect(r.X, r.Y, r.W, r.H), c.Radius, c.Radius);
                    break;
                case CommandKind.FillRect:
                    dc.DrawRectangle(Brush(c.Color), null, new Rect(r.X, r.Y, r.W, r.H));
                    break;
                case CommandKind.StrokeRect:
                case CommandKind.StrokeDashedRect:
                {
                    // Strokes are centred on the edge; inset by half the width so the line stays inside the rect (as the SVG).
                    double hw = c.Radius / 2;
                    var pen = new Pen(Brush(c.Color), c.Radius) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                    if (c.Kind == CommandKind.StrokeDashedRect)
                        pen.DashStyle = new DashStyle([4, 3], 0); // the mock's 4 3 dash, in stroke widths
                    pen.Freeze();
                    dc.DrawRectangle(null, pen, new Rect(r.X + hw, r.Y + hw, Math.Max(0, r.W - c.Radius), Math.Max(0, r.H - c.Radius)));
                    break;
                }
                case CommandKind.FillTriangle:
                {
                    // Tip at the middle of the right edge when Radius > 0, of the left edge otherwise (as the SVG's polygon).
                    double x0 = r.X, x1 = r.X + r.W, y0 = r.Y, y1 = r.Y + r.H, ym = y0 + r.H / 2;
                    bool right = c.Radius > 0;
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(right ? x0 : x1, y0), true, true);
                        ctx.LineTo(new Point(right ? x1 : x0, ym), true, false);
                        ctx.LineTo(new Point(right ? x0 : x1, y1), true, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(Brush(c.Color), null, g);
                    break;
                }
                case CommandKind.Text:
                {
                    double x = c.Continuation && prevText ? prevX + prevAdvance : c.X;
                    var run = fonts.Run(c.Role, c.Weight, c.Italic, list.TextOf(in c), c.SizePx, c.LetterSpacingEm,
                        new Point(x, Math.Round(c.BaselineY, MidpointRounding.ToEven)), pixelsPerDip, out double advance);
                    if (run is not null) dc.DrawGlyphRun(Brush(c.Color), run);
                    prevX = x;
                    prevAdvance = advance;
                    break;
                }
            }
            prevText = c.Kind == CommandKind.Text;
        }
    }

    static SolidColorBrush Brush(Rgba c)
    {
        var b = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}
