using System.Globalization;

namespace FuelPace.Core.Strategy;

/// <summary>Zero-allocation number formatting for the replay printout. Every method returns the chars written; NaN shows as "—".
/// (The overlay itself formats with <see cref="Display.NumberFormat"/>.)</summary>
public static class OverlayText
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Liters(double v, Span<char> dest) => Number(v, "F1", dest);
    public static int PerLap(double v, Span<char> dest) => Number(v, "F2", dest);
    public static int Laps(double v, Span<char> dest) => Number(v, "F1", dest);
    public static int Minutes(double v, Span<char> dest) => Number(v, "F0", dest);
    public static int Seconds(double v, Span<char> dest) => Number(v, "F2", dest);

    static int Number(double v, string format, Span<char> dest)
    {
        var w = new Writer(dest);
        w.Num(v, format);
        return w.Length;
    }

    ref struct Writer
    {
        readonly Span<char> _dest;
        public int Length;

        public Writer(Span<char> dest) { _dest = dest; Length = 0; }

        public void Text(ReadOnlySpan<char> s)
        {
            int n = Math.Min(s.Length, _dest.Length - Length);
            s[..n].CopyTo(_dest[Length..]);
            Length += n;
        }

        public void Num(double v, string format)
        {
            if (double.IsNaN(v)) { Text("—"); return; }
            if (!v.TryFormat(_dest[Length..], out int n, format, Inv)) return;
            Length += n;
        }
    }
}
