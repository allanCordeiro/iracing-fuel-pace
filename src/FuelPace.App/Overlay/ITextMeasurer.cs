namespace FuelPace.App.Overlay;

public interface ITextMeasurer
{
    /// <summary>Advance width in px, letter-spacing included after every char (as CSS does). <paramref name="weight"/> is the
    /// CSS weight the run is drawn with (the same value as <see cref="DrawCommand.Weight"/>), so a renderer whose faces differ
    /// in width per weight measures what it draws.</summary>
    float Width(ReadOnlySpan<char> text, FontRole role, int weight, float sizePx, float letterSpacingEm);

    /// <summary>Distance from the top of a 1.0 line box to the baseline.</summary>
    float Ascent(FontRole role, float sizePx);
}
