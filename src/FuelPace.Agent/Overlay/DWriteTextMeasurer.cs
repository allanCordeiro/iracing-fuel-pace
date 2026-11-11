using FuelPace.App.Overlay;

namespace FuelPace.Agent.Overlay;

/// <summary>
/// <see cref="ITextMeasurer"/> on the faces <see cref="D2DDrawListRenderer"/> draws with (same <see cref="DWriteFonts"/>,
/// role, weight, size and letter spacing), so the layout's positions match the pixels. Widths are cached by a 64-bit hash of
/// the text and its parameters (a hit allocates nothing; a miss builds and drops one text layout). UI thread.
/// </summary>
public sealed class DWriteTextMeasurer(DWriteFonts fonts) : ITextMeasurer
{
    private const int Slots = 1024; // the layout measures about 60 distinct runs per frame, plus the insight cut-off search

    private readonly ulong[] _keys = new ulong[Slots];
    private readonly float[] _widths = new float[Slots];

    public float Width(ReadOnlySpan<char> text, FontRole role, int weight, float sizePx, float letterSpacingEm)
    {
        if (text.IsEmpty) return 0;
        ulong key = Key(text, role, weight, sizePx, letterSpacingEm);
        if (key == 0) key = 1; // 0 marks an empty slot
        int slot = (int)(key % Slots);
        if (_keys[slot] == key) return _widths[slot];
        using (fonts.CreateLayout(new string(text), role, weight, sizePx, letterSpacingEm, false, out float width))
        {
            _keys[slot] = key;
            _widths[slot] = width;
            return width;
        }
    }

    public float Ascent(FontRole role, float sizePx) => fonts.Ascent(role, sizePx);

    internal static ulong Key(ReadOnlySpan<char> text, FontRole role, int weight, float sizePx, float letterSpacingEm)
    {
        ulong h = 14695981039346656037UL;
        h = Mix(h, (ulong)role | ((ulong)(uint)weight << 8) | ((ulong)(uint)text.Length << 32));
        h = Mix(h, BitConverter.SingleToUInt32Bits(sizePx) | ((ulong)BitConverter.SingleToUInt32Bits(letterSpacingEm) << 32));
        foreach (char c in text) h = Mix(h, c);
        return h;
    }

    private static ulong Mix(ulong h, ulong v)
    {
        h ^= v;
        h *= 1099511628211UL;
        h ^= h >> 29;
        return h;
    }
}
