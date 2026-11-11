namespace FuelPace.Core.Laps;

[Flags]
public enum LapReasons : ushort
{
    None = 0,
    Pit = 1,
    Incident = 2,
    OffTrack = 4,
    Caution = 8,
    Reset = 16,
    Refuel = 32,
    Incomplete = 64,
    Outlier = 128,
    Manual = 256,
}

public static class LapReasonCodes
{
    private static readonly (LapReasons Bit, string Code)[] Table =
    [
        (LapReasons.Pit, "pit"),
        (LapReasons.Incident, "incident"),
        (LapReasons.OffTrack, "offtrack"),
        (LapReasons.Caution, "caution"),
        (LapReasons.Reset, "reset"),
        (LapReasons.Refuel, "refuel"),
        (LapReasons.Incomplete, "incomplete"),
        (LapReasons.Outlier, "outlier"),
        (LapReasons.Manual, "manual"),
    ];

    public static string[] ToCodes(LapReasons r)
    {
        int n = 0;
        foreach (var (bit, _) in Table) if ((r & bit) != 0) n++;
        var result = new string[n];
        int i = 0;
        foreach (var (bit, code) in Table) if ((r & bit) != 0) result[i++] = code;
        return result;
    }

    public static LapReasons FromCodes(IEnumerable<string> codes)
    {
        var r = LapReasons.None;
        foreach (var c in codes)
            foreach (var (bit, code) in Table)
                if (string.Equals(c, code, StringComparison.Ordinal)) { r |= bit; break; }
        return r;
    }
}
