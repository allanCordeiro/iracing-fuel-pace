using System.Globalization;

namespace FuelPace.Core.Display;

/// <summary>
/// Zero-allocation number formatting for the v3 overlay (PRD v3 §8.2). Every method writes into <c>dest</c> and returns
/// the chars written (0 when it does not fit). Invariant culture; the minus sign is U+2212 ("−").
/// </summary>
public static class NumberFormat
{
    public const char Minus = '−', Dash = '—';
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary><paramref name="decimals"/> 0..3 places; NaN writes "—"; a negative value uses U+2212.</summary>
    public static int Fixed(float v, int decimals, Span<char> dest)
    {
        if (float.IsNaN(v)) return Put(Dash, dest);
        if (dest.IsEmpty) return 0;
        int n = 0;
        if (v < 0) { dest[n++] = Minus; v = -v; }
        ReadOnlySpan<string> formats = ["F0", "F1", "F2", "F3"];
        return ((double)v).TryFormat(dest[n..], out int w, formats[Math.Clamp(decimals, 0, 3)], Inv) ? n + w : 0;
    }

    /// <summary>A whole number; a negative value writes "—" (the GlanceView's "unavailable" convention).</summary>
    public static int Count(int v, Span<char> dest)
    {
        if (v < 0) return Put(Dash, dest);
        return v.TryFormat(dest, out int w, default, Inv) ? w : 0;
    }

    /// <summary>The red complement: −3 → "−3%".</summary>
    public static int Percent(int signedPct, Span<char> dest)
    {
        int n = 0;
        if (signedPct < 0) { if (dest.IsEmpty) return 0; dest[n++] = Minus; signedPct = -signedPct; }
        if (!signedPct.TryFormat(dest[n..], out int w, default, Inv) || n + w >= dest.Length) return 0;
        n += w;
        dest[n++] = '%';
        return n;
    }

    /// <summary>The red complement in seconds: 0.9 → "+0.9 s", −0.4 → "−0.4 s"; NaN writes "—".</summary>
    public static int SignedSeconds(float v, Span<char> dest)
    {
        if (float.IsNaN(v)) return Put(Dash, dest);
        if (dest.IsEmpty) return 0;
        int n = 0;
        dest[n++] = v < 0 ? Minus : '+';
        if (!Math.Abs((double)v).TryFormat(dest[n..], out int w, "F1", Inv) || n + w + 2 > dest.Length) return 0;
        n += w;
        dest[n++] = ' ';
        dest[n++] = 's';
        return n;
    }

    static int Put(char c, Span<char> dest)
    {
        if (dest.IsEmpty) return 0;
        dest[0] = c;
        return 1;
    }
}
