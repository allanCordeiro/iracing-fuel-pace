using System.Globalization;

namespace FuelPace.Core.Strategy;

/// <summary>Zero-allocation display formatting. Every method returns the chars written; NaN shows as "—".</summary>
public static class OverlayText
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Liters(double v, Span<char> dest) => Number(v, "F1", dest);
    public static int PerLap(double v, Span<char> dest) => Number(v, "F2", dest);
    public static int Laps(double v, Span<char> dest) => Number(v, "F1", dest);
    public static int Minutes(double v, Span<char> dest) => Number(v, "F0", dest);
    public static int Seconds(double v, Span<char> dest) => Number(v, "F2", dest);

    /// <summary>
    /// Writes the insight line (truncated if <paramref name="dest"/> is too small). <paramref name="boldLength"/> is the
    /// leading part up to the first " · " or " (" for insights that have one (decision 9), else 0.
    /// </summary>
    public static int Insight(in Insight insight, ReadOnlySpan<char> carShort, ReadOnlySpan<char> trackShort, Span<char> dest, out int boldLength)
    {
        var w = new Writer(dest);
        boldLength = 0;
        switch (insight.Kind)
        {
            case InsightKind.StopRequired:
                w.Text("Faltam "); w.Num(insight.A, "F1"); w.Text(" L");
                boldLength = w.Length;
                w.Text(" · parada necessária");
                break;
            case InsightKind.BopMismatch:
                w.Text("Baseline com BoP diferente");
                break;
            case InsightKind.Save:
                w.Text("Economize "); w.Num(insight.A, "F2"); w.Text(" L/v");
                boldLength = w.Length;
                w.Text(" ("); w.Text("−"); w.Num(insight.B, "F0"); w.Text("%)");
                if (!double.IsNaN(insight.C))
                {
                    w.Text(" · ≈ ");
                    w.Text(insight.C < 0 ? "−" : "+");
                    w.Num(Math.Abs(insight.C), "F1");
                    w.Text(" s/v");
                }
                break;
            case InsightKind.Slack:
                w.Text("Folga "); w.Num(insight.A, "F1"); w.Text(" L");
                boldLength = w.Length;
                w.Text(" · pode atacar");
                break;
            case InsightKind.OnTarget:
                w.Text("No alvo");
                boldLength = w.Length;
                w.Text(" · margem "); w.Num(insight.A, "F1"); w.Text(" L");
                break;
            case InsightKind.NoBaseline:
                w.Text("Sem baseline para "); w.Text(carShort); w.Text(" · "); w.Text(trackShort);
                break;
            case InsightKind.ElectricUnsupported:
                w.Text("Carro elétrico: sem suporte");
                break;
        }
        if (boldLength > w.Length) boldLength = w.Length;
        return w.Length;
    }

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
