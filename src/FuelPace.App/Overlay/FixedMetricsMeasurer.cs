namespace FuelPace.App.Overlay;

/// <summary>Deterministic metrics for tests and the SVG preview: Mono 0.6 em per char, Inter 0.56 em (capitals 0.68 em), ascent 0.8 em.</summary>
public sealed class FixedMetricsMeasurer : ITextMeasurer
{
    public float Width(ReadOnlySpan<char> text, FontRole role, float sizePx, float letterSpacingEm)
    {
        float em = 0;
        bool mono = role is FontRole.Value or FontRole.ValueBold;
        foreach (char ch in text)
            em += mono ? 0.6f : char.IsUpper(ch) ? 0.68f : 0.56f;
        return (em + text.Length * letterSpacingEm) * sizePx;
    }

    public float Ascent(FontRole role, float sizePx) => 0.8f * sizePx;
}
