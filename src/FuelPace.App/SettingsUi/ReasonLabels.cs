using FuelPace.Core.Laps;

namespace FuelPace.App.SettingsUi;

public static class ReasonLabels
{
    // The order of the mock rows ("pit, incidente", "fora da pista, incidente" reads pit, off-track, incident).
    static readonly (LapReasons Bit, string Label)[] Table =
    [
        (LapReasons.Pit, "pit"),
        (LapReasons.OffTrack, "fora da pista"),
        (LapReasons.Incident, "incidente"),
        (LapReasons.Caution, "bandeira amarela"),
        (LapReasons.Reset, "reset/reboque"),
        (LapReasons.Refuel, "reabastecimento"),
        (LapReasons.Incomplete, "incompleta"),
        (LapReasons.Outlier, "lenta"),
        (LapReasons.Manual, "excluída"),
    ];

    public static string Format(LapReasons r)
    {
        var parts = new List<string>();
        foreach (var (bit, label) in Table)
            if ((r & bit) != 0) parts.Add(label);
        return string.Join(", ", parts);
    }
}
