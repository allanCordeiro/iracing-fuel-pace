namespace FuelPace.App.Overlay;

public interface ITextMeasurer
{
    /// <summary>Advance width in px, letter-spacing included after every char (as CSS does).</summary>
    float Width(ReadOnlySpan<char> text, FontRole role, float sizePx, float letterSpacingEm);

    /// <summary>Distance from the top of a 1.0 line box to the baseline.</summary>
    float Ascent(FontRole role, float sizePx);
}
