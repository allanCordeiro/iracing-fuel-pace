using System.Text;
using System.Windows;
using System.Windows.Media;
using FuelPace.App.Overlay;

namespace FuelPace.Settings;

/// <summary>
/// Decision 8 on the WPF side: Inter and JetBrains Mono from this assembly's resources, one <see cref="GlyphTypeface"/> per file,
/// picked like the Agent's <c>DWriteFonts</c> does: JetBrains Mono for <see cref="FontRole.Value"/>/<see cref="FontRole.ValueBold"/>,
/// Inter otherwise, the file nearest the weight (ties go heavier), italic as an oblique simulation. Text drawn and measured
/// with these faces is a sequence of nominal advances plus the letter spacing after every char (no kerning; DirectWrite
/// kerns, a fraction of a pixel on the overlay's runs). Thread-safe once built.
/// </summary>
public sealed class EmbeddedFonts
{
    public const string FontsBase = "pack://application:,,,/FuelPace.Settings;component/Fonts/";

    static readonly (string File, bool Mono, int Weight)[] Files =
    [
        ("Inter-Regular.ttf", false, 400), ("Inter-SemiBold.ttf", false, 600), ("Inter-Bold.ttf", false, 700),
        ("Inter-ExtraBold.ttf", false, 800),
        ("JetBrainsMono-Regular.ttf", true, 400), ("JetBrainsMono-Medium.ttf", true, 500),
        ("JetBrainsMono-SemiBold.ttf", true, 600), ("JetBrainsMono-Bold.ttf", true, 700),
    ];

    static readonly Lazy<EmbeddedFonts> SharedInstance = new(() => new EmbeddedFonts(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The faces every view of the Settings window uses.</summary>
    public static EmbeddedFonts Shared => SharedInstance.Value;

    readonly GlyphTypeface[] _upright, _italic;
    readonly float[] _baselineEm;

    EmbeddedFonts()
    {
        // The pack://application package is registered by Application's type initializer; tests have no Application object.
        _ = Application.Current;
        _upright = new GlyphTypeface[Files.Length];
        _italic = new GlyphTypeface[Files.Length];
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
        }
    }

    public GlyphTypeface Face(FontRole role, int weight, bool italic = false)
    {
        int i = FaceIndex(role is FontRole.Value or FontRole.ValueBold, weight);
        return italic ? _italic[i] : _upright[i];
    }

    /// <summary>Inter (<paramref name="mono"/> false) or JetBrains Mono for UI text drawn outside a <see cref="DrawList"/>.</summary>
    public GlyphTypeface Face(bool mono, int weight) => _upright[FaceIndex(mono, weight)];

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
    public float Ascent(FontRole role, float sizePx) => _baselineEm[FaceIndex(role is FontRole.Value or FontRole.ValueBold, 400)] * sizePx;

    /// <summary>Advance of <paramref name="text"/> in px, <paramref name="letterSpacingEm"/> added after every char (as CSS).</summary>
    public float Width(ReadOnlySpan<char> text, FontRole role, int weight, float sizePx, float letterSpacingEm) =>
        (float)Advance(Face(role, weight), text, sizePx, letterSpacingEm);

    internal static double Advance(GlyphTypeface face, ReadOnlySpan<char> text, double sizePx, double letterSpacingEm)
    {
        double w = 0, spacing = letterSpacingEm * sizePx;
        var map = face.CharacterToGlyphMap;
        var advances = face.AdvanceWidths;
        foreach (var rune in text.EnumerateRunes())
        {
            map.TryGetValue(rune.Value, out ushort glyph);
            w += advances[glyph] * sizePx + spacing * rune.Utf16SequenceLength;
        }
        return w;
    }

    /// <summary>
    /// A glyph run of <paramref name="text"/> with its baseline at <paramref name="origin"/>, or null for empty text.
    /// <paramref name="advance"/> is its width, letter spacing included (as <see cref="Width"/>).
    /// </summary>
    internal static GlyphRun? Run(GlyphTypeface face, ReadOnlySpan<char> text, double sizePx, double letterSpacingEm, Point origin,
        double pixelsPerDip, out double advance)
    {
        advance = 0;
        if (text.IsEmpty) return null;
        double spacing = letterSpacingEm * sizePx;
        var map = face.CharacterToGlyphMap;
        var widths = face.AdvanceWidths;
        var glyphs = new List<ushort>(text.Length);
        var advances = new List<double>(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            map.TryGetValue(rune.Value, out ushort glyph);
            double a = widths[glyph] * sizePx + spacing * rune.Utf16SequenceLength;
            glyphs.Add(glyph);
            advances.Add(a);
            advance += a;
        }
        return new GlyphRun(face, 0, false, sizePx, (float)pixelsPerDip, glyphs, origin, advances, null, null, null, null, null,
            null);
    }
}
