using FuelPace.App.Overlay;

namespace FuelPace.Settings;

/// <summary><see cref="ITextMeasurer"/> on the faces <see cref="WpfDrawListRenderer"/> draws with, so the preview's layout matches
/// its pixels (role, weight, size and letter spacing, as the Agent's <c>DWriteTextMeasurer</c>).</summary>
public sealed class WpfTextMeasurer(EmbeddedFonts fonts) : ITextMeasurer
{
    public float Width(ReadOnlySpan<char> text, FontRole role, int weight, float sizePx, float letterSpacingEm) =>
        fonts.Width(text, role, weight, sizePx, letterSpacingEm);

    public float Ascent(FontRole role, float sizePx) => fonts.Ascent(role, sizePx);
}
