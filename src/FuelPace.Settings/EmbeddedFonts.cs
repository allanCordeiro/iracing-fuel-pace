using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using FuelPace.App.Overlay;

namespace FuelPace.Settings;

/// <summary>
/// Decision 8 on the WPF side: Inter and JetBrains Mono from this assembly's resources, one <see cref="GlyphTypeface"/> per file,
/// picked like the Agent's <c>DWriteFonts</c> does: JetBrains Mono for <see cref="FontRole.Value"/>/<see cref="FontRole.ValueBold"/>,
/// Inter otherwise, the file nearest the weight (ties go heavier), italic as an oblique simulation.
/// <para>A run's advance is the glyphs' nominal advances, plus Inter's pair kerning (as DirectWrite applies it), plus the letter
/// spacing after every char (as CSS). Each kerning pair is measured once, with WPF's own text formatting on the same file, and
/// cached. Thread-safe.</para>
/// </summary>
public sealed class EmbeddedFonts
{
    public const string FontsBase = "pack://application:,,,/FuelPace.Settings;component/Fonts/";

    static readonly (string File, string Family, bool Mono, int Weight)[] Files =
    [
        ("Inter-Regular.ttf", "Inter", false, 400), ("Inter-SemiBold.ttf", "Inter", false, 600), ("Inter-Bold.ttf", "Inter", false, 700),
        ("Inter-ExtraBold.ttf", "Inter", false, 800),
        ("JetBrainsMono-Regular.ttf", "JetBrains Mono", true, 400), ("JetBrainsMono-Medium.ttf", "JetBrains Mono", true, 500),
        ("JetBrainsMono-SemiBold.ttf", "JetBrains Mono", true, 600), ("JetBrainsMono-Bold.ttf", "JetBrains Mono", true, 700),
    ];

    const double KernEm = 100; // pairs are measured at this size and scaled

    static readonly Lazy<EmbeddedFonts> SharedInstance = new(() => new EmbeddedFonts(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The faces every view of the Settings window uses.</summary>
    public static EmbeddedFonts Shared => SharedInstance.Value;

    readonly GlyphTypeface[] _upright, _italic;
    readonly Typeface?[] _kernTypefaces;
    readonly float[] _baselineEm;
    readonly Dictionary<(int Face, int A, int B), double> _kern = [];
    readonly Lock _kernGate = new();

    EmbeddedFonts()
    {
        // The pack://application package is registered by Application's type initializer; tests have no Application object.
        _ = Application.Current;
        _upright = new GlyphTypeface[Files.Length];
        _italic = new GlyphTypeface[Files.Length];
        _kernTypefaces = new Typeface?[Files.Length];
        _baselineEm = new float[Files.Length];
        for (int i = 0; i < Files.Length; i++)
        {
            var uri = new Uri(FontsBase + Files[i].File);
            var g = _upright[i] = new GlyphTypeface(uri);
            _italic[i] = new GlyphTypeface(uri, StyleSimulations.ItalicSimulation);
            if (g.Weight.ToOpenTypeWeight() != Files[i].Weight)
                throw new InvalidOperationException($"embedded font {Files[i].File}: weight {g.Weight.ToOpenTypeWeight()}");
            // As DWriteFonts: the top of a CSS line box of height 1 em to the baseline, ½ + (ascent − descent) / 2.
            double ascent = g.Baseline, descent = g.Height - g.Baseline;
            _baselineEm[i] = (float)(0.5 + (ascent - descent) / 2);
            if (!Files[i].Mono) _kernTypefaces[i] = KernTypeface(i);
        }
    }

    /// <summary>The same file as a <see cref="Typeface"/> for <see cref="FormattedText"/> (which kerns), or null when the
    /// family lookup would not land on exactly that file (no kerning then, rather than another face's).</summary>
    static Typeface? KernTypeface(int i)
    {
        var family = new FontFamily(new Uri(FontsBase), "./#" + Files[i].Family);
        var t = new Typeface(family, FontStyles.Normal, FontWeight.FromOpenTypeWeight(Files[i].Weight), FontStretches.Normal);
        return t.TryGetGlyphTypeface(out var g) && g.StyleSimulations == StyleSimulations.None
            && g.FontUri.OriginalString.EndsWith(Files[i].File, StringComparison.OrdinalIgnoreCase) ? t : null;
    }

    public GlyphTypeface Face(FontRole role, int weight, bool italic = false)
    {
        int i = FaceIndex(IsMono(role), weight);
        return italic ? _italic[i] : _upright[i];
    }

    /// <summary>Whether runs in <paramref name="role"/> at <paramref name="weight"/> are kerned (Inter, when its file resolved).</summary>
    public bool Kerns(FontRole role, int weight) => _kernTypefaces[FaceIndex(IsMono(role), weight)] is not null;

    static bool IsMono(FontRole role) => role is FontRole.Value or FontRole.ValueBold;

    static int FaceIndex(bool mono, int weight)
    {
        int best = -1, bestDist = int.MaxValue;
        for (int i = 0; i < Files.Length; i++)
        {
            if (Files[i].Mono != mono) continue;
            int d = Math.Abs(Files[i].Weight - weight);
            if (d < bestDist || (d == bestDist && Files[i].Weight > Files[best].Weight)) { best = i; bestDist = d; }
        }
        return best;
    }

    /// <summary>Top of a 1.0 line box to the baseline (the same for every weight of a family), as the Agent.</summary>
    public float Ascent(FontRole role, float sizePx) => _baselineEm[FaceIndex(IsMono(role), 400)] * sizePx;

    /// <summary>Advance of <paramref name="text"/> in px, kerning included and <paramref name="letterSpacingEm"/> added after every
    /// char (as CSS).</summary>
    public float Width(ReadOnlySpan<char> text, FontRole role, int weight, float sizePx, float letterSpacingEm)
    {
        double w = 0;
        Layout(FaceIndex(IsMono(role), weight), false, text, sizePx, letterSpacingEm, null, null, ref w);
        return (float)w;
    }

    /// <summary>
    /// A glyph run of <paramref name="text"/> with its baseline at <paramref name="origin"/>, or null for empty text.
    /// <paramref name="advance"/> is its width (as <see cref="Width"/>).
    /// </summary>
    public GlyphRun? Run(FontRole role, int weight, bool italic, ReadOnlySpan<char> text, double sizePx, double letterSpacingEm,
        Point origin, double pixelsPerDip, out double advance)
    {
        advance = 0;
        if (text.IsEmpty) return null;
        int face = FaceIndex(IsMono(role), weight);
        var glyphs = new List<ushort>(text.Length);
        var advances = new List<double>(text.Length);
        Layout(face, italic, text, sizePx, letterSpacingEm, glyphs, advances, ref advance);
        return new GlyphRun(italic ? _italic[face] : _upright[face], 0, false, sizePx, (float)pixelsPerDip, glyphs, origin, advances,
            null, null, null, null, null, null);
    }

    void Layout(int face, bool italic, ReadOnlySpan<char> text, double sizePx, double letterSpacingEm, List<ushort>? glyphs,
        List<double>? advances, ref double width)
    {
        var g = italic ? _italic[face] : _upright[face];
        var map = g.CharacterToGlyphMap;
        var widths = g.AdvanceWidths;
        double spacing = letterSpacingEm * sizePx;
        int prev = -1;
        int last = advances?.Count ?? 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            map.TryGetValue(rune.Value, out ushort glyph);
            double kern = prev >= 0 ? Kern(face, prev, rune.Value) * sizePx : 0;
            width += kern + widths[glyph] * sizePx + spacing * rune.Utf16SequenceLength;
            if (advances is not null)
            {
                if (advances.Count > last) advances[^1] += kern; // the pair's kerning moves this glyph, as DirectWrite
                glyphs!.Add(glyph);
                advances.Add(widths[glyph] * sizePx + spacing * rune.Utf16SequenceLength);
            }
            prev = rune.Value;
        }
    }

    /// <summary>Kerning between two chars in em (0 for JetBrains Mono).</summary>
    double Kern(int face, int a, int b)
    {
        if (_kernTypefaces[face] is not { } t) return 0;
        lock (_kernGate)
        {
            if (_kern.TryGetValue((face, a, b), out double k)) return k;
            string sa = char.ConvertFromUtf32(a), sb = char.ConvertFromUtf32(b);
            k = (Measure(t, sa + sb) - Measure(t, sa) - Measure(t, sb)) / KernEm;
            if (Math.Abs(k) < 1e-4) k = 0;
            _kern[(face, a, b)] = k;
            return k;
        }
    }

    static double Measure(Typeface t, string s) =>
        new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, t, KernEm, Brushes.Black, 1.0)
            .WidthIncludingTrailingWhitespace;
}
