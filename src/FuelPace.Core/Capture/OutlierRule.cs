using FuelPace.Core.Laps;

namespace FuelPace.Core.Capture;

/// <summary>§6.3 <c>outlier</c>: time &gt; 107% of the median of the <b>other</b> valid laps (at least 2 of them).</summary>
public static class OutlierRule
{
    public const double Factor = 1.07;

    /// <summary>
    /// Recomputes <see cref="LapReasons.Outlier"/> from scratch (idempotent). The base is the laps with no other
    /// reason and a time; every lap is judged against that same base.
    /// </summary>
    public static void Apply(IList<CapturedLap> laps)
    {
        foreach (var l in laps) l.Reasons &= ~LapReasons.Outlier;

        var basis = new List<CapturedLap>();
        foreach (var l in laps)
            if (l.Reasons == LapReasons.None && l.TimeS is not null) basis.Add(l);
        if (basis.Count < 3) return;

        var flagged = new List<CapturedLap>();
        var others = new List<double>(basis.Count - 1);
        foreach (var l in basis)
        {
            others.Clear();
            foreach (var o in basis)
                if (!ReferenceEquals(o, l)) others.Add(o.TimeS!.Value);
            if (l.TimeS!.Value > Factor * Median(others)) flagged.Add(l);
        }
        foreach (var l in flagged) l.Reasons |= LapReasons.Outlier;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}
