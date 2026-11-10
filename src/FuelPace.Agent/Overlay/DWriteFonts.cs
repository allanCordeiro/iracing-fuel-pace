using System.Reflection;
using FuelPace.App.Overlay;
using Vortice.DirectWrite;

namespace FuelPace.Agent.Overlay;

/// <summary>One embedded font file: its family as DirectWrite reads it, its weight and its line-height-1.0 baseline.</summary>
public sealed class EmbeddedFace
{
    internal EmbeddedFace(string file, string family, int weight, bool mono, float baselineEm, IDWriteFontCollection1 collection)
    {
        File = file; Family = family; Weight = weight; Mono = mono; BaselineEm = baselineEm; Collection = collection;
    }

    public string File { get; }
    public string Family { get; }
    public int Weight { get; }
    public bool Mono { get; }
    /// <summary>Top of a CSS line box of height 1 em to the baseline, in em: ½ + (ascent − descent) / 2.</summary>
    public float BaselineEm { get; }
    internal IDWriteFontCollection1 Collection { get; }
}

/// <summary>
/// Decision 8: Inter (labels, prose) and JetBrains Mono (numbers), from the TTFs embedded in the Agent
/// (<c>assets/fonts</c>), loaded through DirectWrite's in-memory loader; nothing is installed on the system. Each file
/// gets a collection of its own, so a run asks for exactly the face it names (weight 600 is Inter SemiBold, never a bold
/// simulation, whatever the files' family names). The weights a run asks for map to the nearest file (ties go heavier);
/// italic runs get DirectWrite's oblique simulation (no italic files are shipped).
/// Text formats are cached here per (face, size, italic). Use on one thread (the Agent's UI thread).
/// </summary>
public sealed class DWriteFonts : IDisposable
{
    private static readonly (string File, bool Mono, int Weight)[] Files =
    [
        ("Inter-Regular.ttf", false, 400), ("Inter-SemiBold.ttf", false, 600), ("Inter-Bold.ttf", false, 700),
        ("Inter-ExtraBold.ttf", false, 800),
        ("JetBrainsMono-Regular.ttf", true, 400), ("JetBrainsMono-Medium.ttf", true, 500),
        ("JetBrainsMono-SemiBold.ttf", true, 600), ("JetBrainsMono-Bold.ttf", true, 700),
    ];

    private const int MaxFormats = 48;
    private const string Locale = "pt-br";

    private readonly IDWriteFactory5 _factory;
    private readonly IDWriteInMemoryFontFileLoader _loader;
    private readonly List<IDisposable> _owned = [];
    private readonly EmbeddedFace[] _faces;
    private readonly List<(int Face, float Size, bool Italic, IDWriteTextFormat Format)> _formats = [];
    private bool _disposed;

    public DWriteFonts()
    {
        _factory = DWrite.DWriteCreateFactory<IDWriteFactory5>(FactoryType.Shared);
        _loader = _factory.CreateInMemoryFontFileLoader();
        _factory.RegisterFontFileLoader(_loader);
        try
        {
            _faces = new EmbeddedFace[Files.Length];
            for (int i = 0; i < Files.Length; i++) _faces[i] = Load(Files[i].File, Files[i].Mono, Files[i].Weight);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IDWriteFactory5 Factory => _factory;

    private unsafe EmbeddedFace Load(string file, bool mono, int weight)
    {
        byte[] data = ReadResource(file);
        IDWriteFontFile fontFile;
        fixed (byte* p = data) // a null owner makes DirectWrite copy the data
            fontFile = _loader.CreateInMemoryFontFileReference(_factory, (nint)p, (uint)data.Length, null);
        _owned.Add(fontFile);
        using var builder = _factory.CreateFontSetBuilder();
        builder.AddFontFile(fontFile);
        var set = builder.CreateFontSet();
        _owned.Add(set);
        var collection = _factory.CreateFontCollectionFromFontSet(set);
        _owned.Add(collection);
        if (collection.FontFamilyCount != 1) throw new InvalidOperationException($"embedded font {file}: {collection.FontFamilyCount} families");

        using var family = collection.GetFontFamily(0);
        using var names = family.FamilyNames;
        string name = names.GetString(0);
        using var font = family.GetFirstMatchingFont((FontWeight)weight, FontStretch.Normal, FontStyle.Normal);
        if ((int)font.Weight != weight || font.Simulations != FontSimulations.None)
            throw new InvalidOperationException($"embedded font {file}: weight {(int)font.Weight}, simulations {font.Simulations}");
        var m = font.Metrics;
        float baseline = 0.5f + (m.Ascent - m.Descent) / 2f / m.DesignUnitsPerEm;
        return new EmbeddedFace(file, name, weight, mono, baseline, collection);
    }

    private static byte[] ReadResource(string file)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("FuelPace.Fonts." + file)
            ?? throw new InvalidOperationException("embedded font missing: " + file);
        var data = new byte[s.Length];
        s.ReadExactly(data);
        return data;
    }

    /// <summary>JetBrains Mono for <see cref="FontRole.Value"/> and <see cref="FontRole.ValueBold"/>, Inter otherwise; the file
    /// nearest <paramref name="weight"/>.</summary>
    public EmbeddedFace Face(FontRole role, int weight) => _faces[FaceIndex(role, weight)];

    private int FaceIndex(FontRole role, int weight)
    {
        bool mono = role is FontRole.Value or FontRole.ValueBold;
        int best = -1, bestDist = int.MaxValue;
        for (int i = 0; i < _faces.Length; i++)
        {
            var f = _faces[i];
            if (f.Mono != mono) continue;
            int d = Math.Abs(f.Weight - weight);
            if (d < bestDist || (d == bestDist && f.Weight > _faces[best].Weight)) { best = i; bestDist = d; }
        }
        return best;
    }

    /// <summary>Top of a 1.0 line box to the baseline (the same for every weight of a family).</summary>
    public float Ascent(FontRole role, float sizePx) => Face(role, 400).BaselineEm * sizePx;

    /// <summary>
    /// A single-line layout of <paramref name="text"/> in the face for (<paramref name="role"/>, <paramref name="weight"/>),
    /// <paramref name="letterSpacingEm"/> added after every char as CSS does; its origin is the top of a 1.0 line box, so the
    /// baseline is at <see cref="Ascent"/>. <paramref name="width"/> is the advance, trailing spaces and spacing included.
    /// </summary>
    public IDWriteTextLayout CreateLayout(string text, FontRole role, int weight, float sizePx, float letterSpacingEm, bool italic,
        out float width)
    {
        var format = Format(FaceIndex(role, weight), sizePx, italic);
        var layout = _factory.CreateTextLayout(text, format, 100_000, sizePx * 4);
        if (letterSpacingEm != 0 && text.Length > 0)
        {
            using var l1 = layout.QueryInterface<IDWriteTextLayout1>();
            l1.SetCharacterSpacing(0, letterSpacingEm * sizePx, 0, new TextRange { StartPosition = 0, Length = (uint)text.Length });
        }
        width = layout.Metrics.WidthIncludingTrailingWhitespace;
        return layout;
    }

    private IDWriteTextFormat Format(int face, float size, bool italic)
    {
        foreach (var f in _formats)
            if (f.Face == face && f.Size == size && f.Italic == italic) return f.Format;
        if (_formats.Count == MaxFormats)
        {
            _formats[0].Format.Dispose(); // the oldest; layouts made from it keep their own copy of its properties
            _formats.RemoveAt(0);
        }
        var e = _faces[face];
        var format = _factory.CreateTextFormat(e.Family, e.Collection, (FontWeight)e.Weight,
            italic ? FontStyle.Italic : FontStyle.Normal, FontStretch.Normal, size, Locale);
        format.WordWrapping = WordWrapping.NoWrap;
        format.SetLineSpacing(LineSpacingMethod.Uniform, size, e.BaselineEm * size);
        _formats.Add((face, size, italic, format));
        return format;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var f in _formats) f.Format.Dispose();
        _formats.Clear();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
        _factory.UnregisterFontFileLoader(_loader);
        _loader.Dispose();
        _factory.Dispose();
    }
}
