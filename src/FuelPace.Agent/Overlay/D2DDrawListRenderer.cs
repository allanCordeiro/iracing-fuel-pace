using System.Numerics;
using FuelPace.App.Overlay;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace FuelPace.Agent.Overlay;

/// <summary>
/// Draws a <see cref="DrawList"/> on a Direct2D render target (the composition back buffer, the layered window's DIB or an
/// offscreen bitmap), between the caller's <c>BeginDraw</c> and <c>EndDraw</c>, with the target's current transform.
/// Colors are straight alpha (Direct2D premultiplies into the premultiplied target); text is grayscale-antialiased (no
/// ClearType on a transparent surface). Text uses each command's role, weight, size, letter spacing and italic; a
/// <see cref="DrawCommand.Continuation"/> run starts where the previous run's measured advance ends, on the same baseline;
/// baselines are snapped to whole pixels.
/// <para>Text layouts are cached per (text, role, weight, size, spacing, italic) in an LRU of <c>capacity</c> entries: a
/// frame whose texts are cached allocates nothing managed (Vortice's wrappers are created on a miss only).</para>
/// One instance per render target; UI thread.
/// </summary>
public sealed class D2DDrawListRenderer : IDisposable
{
    private sealed class Entry
    {
        public ulong Key;
        public string Text = "";
        public FontRole Role;
        public int Weight;
        public float Size, Spacing;
        public bool Italic;
        public IDWriteTextLayout? Layout;
        public float Width, Baseline;
        public long Used;
    }

    private readonly ID2D1RenderTarget _target;
    private readonly DWriteFonts _fonts;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly ID2D1StrokeStyle _dashed;
    private readonly Entry[] _cache;
    private long _clock, _created;
    private bool _disposed;

    public D2DDrawListRenderer(ID2D1RenderTarget target, DWriteFonts fonts, int capacity = 64)
    {
        _target = target;
        _fonts = fonts;
        _target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _target.AntialiasMode = AntialiasMode.PerPrimitive;
        _brush = target.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        using (var factory = target.Factory)
            _dashed = factory.CreateStrokeStyle(new StrokeStyleProperties
            {
                StartCap = CapStyle.Flat, EndCap = CapStyle.Flat, DashCap = CapStyle.Flat,
                LineJoin = LineJoin.Miter, MiterLimit = 10, DashStyle = DashStyle.Custom,
            }, [4, 3]); // the mock's 4 3 dash, in stroke widths (1 px at 1x, so it follows the DPI)
        _cache = new Entry[capacity];
        for (int i = 0; i < capacity; i++) _cache[i] = new Entry();
    }

    /// <summary>Text layouts built so far (cache misses).</summary>
    public long LayoutsCreated => _created;

    public void Draw(DrawList list)
    {
        float prevX = 0, prevAdvance = 0;
        bool prevText = false;
        foreach (ref readonly var c in list.Commands)
        {
            _brush.Color = new Color4(c.Color.R / 255f, c.Color.G / 255f, c.Color.B / 255f, c.Color.A / 255f);
            var r = c.Rect;
            switch (c.Kind)
            {
                case CommandKind.FillRoundedRect:
                    _target.FillRoundedRectangle(new RoundedRectangle
                    {
                        Rect = new RawRectF(r.X, r.Y, r.X + r.W, r.Y + r.H), RadiusX = c.Radius, RadiusY = c.Radius,
                    }, _brush);
                    break;
                case CommandKind.FillRect:
                    _target.FillRectangle(new RawRectF(r.X, r.Y, r.X + r.W, r.Y + r.H), _brush);
                    break;
                case CommandKind.StrokeRect:
                case CommandKind.StrokeDashedRect:
                    // Direct2D centres strokes on the edge; inset by half the width so the line stays inside the rect (as the SVG).
                    float hw = c.Radius / 2;
                    _target.DrawRectangle(new RawRectF(r.X + hw, r.Y + hw, r.X + r.W - hw, r.Y + r.H - hw), _brush, c.Radius,
                        c.Kind == CommandKind.StrokeDashedRect ? _dashed : null);
                    break;
                case CommandKind.Text:
                    var e = Layout(list.TextOf(in c), c.Role, c.Weight, c.SizePx, c.LetterSpacingEm, c.Italic);
                    float x = c.Continuation && prevText ? prevX + prevAdvance : c.X;
                    _target.DrawTextLayout(new Vector2(x, MathF.Round(c.BaselineY) - e.Baseline), e.Layout!, _brush, DrawTextOptions.None);
                    prevX = x;
                    prevAdvance = e.Width;
                    break;
            }
            prevText = c.Kind == CommandKind.Text;
        }
    }

    private Entry Layout(ReadOnlySpan<char> text, FontRole role, int weight, float size, float spacing, bool italic)
    {
        ulong key = DWriteTextMeasurer.Key(text, role, weight, size, spacing) ^ (italic ? 0x9E3779B97F4A7C15UL : 0);
        long now = ++_clock;
        Entry? victim = null;
        foreach (var e in _cache)
        {
            if (e.Layout is not null && e.Key == key && e.Role == role && e.Weight == weight && e.Size == size
                && e.Spacing == spacing && e.Italic == italic && text.SequenceEqual(e.Text))
            {
                e.Used = now;
                return e;
            }
            if (victim is null || e.Used < victim.Used) victim = e;
        }
        var v = victim!;
        v.Layout?.Dispose();
        v.Text = new string(text);
        v.Key = key; v.Role = role; v.Weight = weight; v.Size = size; v.Spacing = spacing; v.Italic = italic; v.Used = now;
        v.Layout = _fonts.CreateLayout(v.Text, role, weight, size, spacing, italic, out v.Width);
        v.Baseline = _fonts.Ascent(role, size);
        _created++;
        return v;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var e in _cache)
        {
            e.Layout?.Dispose();
            e.Layout = null;
        }
        _dashed.Dispose();
        _brush.Dispose();
    }
}
