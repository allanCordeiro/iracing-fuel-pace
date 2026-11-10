namespace FuelPace.App.Overlay;

/// <summary>Deterministic metrics for tests and the SVG preview: Mono 0.6 em per char, Inter about 0.54 em (per-class estimates; capitals 0.68, digits 0.58, space 0.28), ascent 0.8 em.</summary>
public sealed class FixedMetricsMeasurer : ITextMeasurer
{
    public float Width(ReadOnlySpan<char> text, FontRole role, float sizePx, float letterSpacingEm)
    {
        float em = 0;
        bool mono = role is FontRole.Value or FontRole.ValueBold;
        foreach (char ch in text)
            em += mono ? 0.6f : InterEm(ch);
        return (em + text.Length * letterSpacingEm) * sizePx;
    }

    // Close to real Inter for mixed-case Portuguese text.
    static float InterEm(char ch) =>
        char.IsUpper(ch) ? 0.68f
        : char.IsDigit(ch) ? 0.58f
        : ch == ' ' ? 0.28f
        : ch is '.' or ',' or ':' or ';' or '·' or '\'' or 'i' or 'l' or 'j' or '|' ? 0.28f
        : ch is 'm' or 'w' ? 0.8f
        : ch is 'f' or 't' or 'r' or '(' or ')' ? 0.38f
        : 0.54f;

    public float Ascent(FontRole role, float sizePx) => 0.8f * sizePx;
}
