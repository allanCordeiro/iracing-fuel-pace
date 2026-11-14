namespace FuelPace.App.Overlay;

/// <summary>Inter for labels and prose, JetBrains Mono for numbers.</summary>
public enum FontRole : byte { Label, Value, ValueBold, Body, BodyBold, Badge }

public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    /// <summary>Color from a CSS-style alpha in 0..1.</summary>
    public static Rgba From(byte r, byte g, byte b, double alpha) => new(r, g, b, ToByte(alpha));

    public static Rgba Hex(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);

    /// <summary>Same color with the alpha multiplied by <paramref name="f"/> (clamped to 0..1).</summary>
    public Rgba WithOpacity(double f) => this with { A = ToByte(A / 255.0 * Math.Clamp(f, 0, 1)) };

    static byte ToByte(double alpha) => (byte)Math.Clamp(Math.Round(alpha * 255, MidpointRounding.AwayFromZero), 0, 255);
}

public readonly record struct RectF(float X, float Y, float W, float H);

public readonly record struct SizeF(float W, float H);

public enum CommandKind : byte { FillRoundedRect, StrokeDashedRect, FillRect, StrokeRect, Text, FillTriangle }

/// <summary>
/// One draw command. For <see cref="CommandKind.Text"/>, <c>Rect.X</c>/<c>Rect.Y</c> are x and the baseline y (W/H unused).
/// <c>Weight</c> is the CSS font weight (400..800). <c>Continuation</c> marks a text run that follows the previous run on the
/// same baseline (flow-capable renderers such as SVG may ignore its X and let the text flow). <c>Radius</c> is the corner radius for rounded fills and the line width for strokes.
/// <see cref="CommandKind.FillTriangle"/> fills an isosceles triangle inside <c>Rect</c>: its tip at the middle of the right
/// edge when <c>Radius</c> &gt; 0, of the left edge otherwise (the base is the opposite edge).
/// </summary>
public readonly record struct DrawCommand(
    CommandKind Kind, RectF Rect, float Radius, Rgba Color,
    int TextStart, int TextLength, FontRole Role, float SizePx, float LetterSpacingEm, bool Italic, int Weight = 400, bool Continuation = false)
{
    public float X => Rect.X;
    public float BaselineY => Rect.Y;
}

/// <summary>
/// Reusable list of draw commands. Grows during warm-up only; after that <see cref="Clear"/> + adds allocate nothing.
/// All text lives in one internal char arena.
/// </summary>
public sealed class DrawList
{
    DrawCommand[] _cmds = new DrawCommand[64];
    char[] _arena = new char[512];
    int _count, _arenaLen;
    ulong _hash = Seed;
    const ulong Seed = 14695981039346656037UL;

    public SizeF Size { get; set; }

    public ReadOnlySpan<DrawCommand> Commands => new(_cmds, 0, _count);

    public ReadOnlySpan<char> TextOf(in DrawCommand c) => new(_arena, c.TextStart, c.TextLength);

    /// <summary>Hash of every command and text (and the size): equal hashes mean an identical picture.</summary>
    public long ContentHash => (long)Mix(_hash, BitConverter.SingleToUInt32Bits(Size.W) | ((ulong)BitConverter.SingleToUInt32Bits(Size.H) << 32));

    public void Clear()
    {
        _count = 0; _arenaLen = 0; _hash = Seed; Size = default;
    }

    public void FillRoundedRect(RectF r, float radius, Rgba color) => Add(new(CommandKind.FillRoundedRect, r, radius, color, 0, 0, default, 0, 0, false));
    public void StrokeDashedRect(RectF r, float width, Rgba color) => Add(new(CommandKind.StrokeDashedRect, r, width, color, 0, 0, default, 0, 0, false));
    public void FillRect(RectF r, Rgba color) => Add(new(CommandKind.FillRect, r, 0, color, 0, 0, default, 0, 0, false));
    public void StrokeRect(RectF r, float width, Rgba color) => Add(new(CommandKind.StrokeRect, r, width, color, 0, 0, default, 0, 0, false));

    /// <summary>A triangle inside <paramref name="r"/> pointing right (tip at the middle of the right edge) or left.</summary>
    public void FillTriangle(RectF r, bool pointsRight, Rgba color) => Add(new(CommandKind.FillTriangle, r, pointsRight ? 1 : -1, color, 0, 0, default, 0, 0, false));

    /// <summary>Copies <paramref name="text"/> into the arena and returns its start.</summary>
    public int Append(ReadOnlySpan<char> text)
    {
        int start = _arenaLen;
        if (start + text.Length > _arena.Length) Array.Resize(ref _arena, Math.Max(_arena.Length * 2, start + text.Length));
        text.CopyTo(_arena.AsSpan(start));
        _arenaLen += text.Length;
        return start;
    }

    public void Text(int arenaStart, int length, FontRole role, int weight, float sizePx, float letterSpacingEm, bool italic, Rgba color, float x, float baselineY, bool continuation = false)
        => Add(new(CommandKind.Text, new RectF(x, baselineY, 0, 0), 0, color, arenaStart, length, role, sizePx, letterSpacingEm, italic, weight, continuation));

    public void Text(ReadOnlySpan<char> text, FontRole role, int weight, float sizePx, float letterSpacingEm, bool italic, Rgba color, float x, float baselineY, bool continuation = false)
        => Text(Append(text), text.Length, role, weight, sizePx, letterSpacingEm, italic, color, x, baselineY, continuation);

    void Add(in DrawCommand c)
    {
        if (_count == _cmds.Length) Array.Resize(ref _cmds, _cmds.Length * 2);
        _cmds[_count++] = c;
        ulong h = _hash;
        h = Mix(h, (ulong)c.Kind | ((ulong)c.Role << 8) | (c.Italic ? 1UL << 16 : 0) | (c.Continuation ? 1UL << 17 : 0) | ((ulong)(uint)c.Weight << 18) | ((ulong)(uint)c.TextLength << 32));
        h = Mix(h, BitConverter.SingleToUInt32Bits(c.Rect.X) | ((ulong)BitConverter.SingleToUInt32Bits(c.Rect.Y) << 32));
        h = Mix(h, BitConverter.SingleToUInt32Bits(c.Rect.W) | ((ulong)BitConverter.SingleToUInt32Bits(c.Rect.H) << 32));
        h = Mix(h, BitConverter.SingleToUInt32Bits(c.Radius) | ((ulong)BitConverter.SingleToUInt32Bits(c.SizePx) << 32));
        h = Mix(h, BitConverter.SingleToUInt32Bits(c.LetterSpacingEm) | ((ulong)(c.Color.R | (c.Color.G << 8) | (c.Color.B << 16)) << 32) | ((ulong)c.Color.A << 56));
        for (int i = 0; i < c.TextLength; i++) h = Mix(h, _arena[c.TextStart + i]);
        _hash = h;
    }

    static ulong Mix(ulong h, ulong v)
    {
        h ^= v;
        h *= 1099511628211UL; // FNV-1a prime
        h ^= h >> 29;
        return h;
    }
}
