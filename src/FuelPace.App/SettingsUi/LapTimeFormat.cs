using System.Globalization;

namespace FuelPace.App.SettingsUi;

public static class LapTimeFormat
{
    public const string None = "—";

    /// <summary><c>1:20.897</c>; a missing, NaN or negative time is <c>—</c>.</summary>
    public static string Format(double? seconds)
    {
        if (seconds is not { } s || double.IsNaN(s) || s < 0 || s > 1e6) return None;
        long ms = (long)Math.Round((decimal)s * 1000m, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 60000}:{ms % 60000 / 1000:00}.{ms % 1000:000}");
    }
}
