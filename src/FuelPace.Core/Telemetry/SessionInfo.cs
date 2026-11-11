namespace FuelPace.Core.Telemetry;

public sealed record SessionDef(int Num, string Type, int? Laps, double? TimeS);

public sealed record SessionInfo(
    int DriverCarIdx,
    string CarPath,
    string CarScreenName,
    string CarScreenNameShort,
    string TrackName,
    string TrackDisplayShortName,
    string TrackDisplayName,
    string TrackConfigName,
    double TrackLengthKm,
    double TankCapacityL,
    double FuelKgPerL,
    double EstLapTimeS,
    IReadOnlyList<SessionDef> Sessions)
{
    public SessionDef? FindSession(int num)
    {
        foreach (var s in Sessions)
            if (s.Num == num) return s;
        return null;
    }

    public string ProfileKey => $"{CarPath}__{TrackName}";
}
