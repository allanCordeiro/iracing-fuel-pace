using System.Globalization;

namespace FuelPace.App.SettingsUi;

/// <summary>
/// Ruling A25: the Settings fields show numbers with a decimal point (as the overlay and the mocks) and accept a comma or a
/// point typed by the pilot. No thousands separators; NaN and infinities are not numbers here.
/// </summary>
public static class NumberInput
{
    public static string Format(double v) => v.ToString(CultureInfo.InvariantCulture);

    public static string Format(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    public static bool TryParseInt(string? text, out int value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text)
            && int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
