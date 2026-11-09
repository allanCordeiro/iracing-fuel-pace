using FuelPace.Core.Laps;

namespace FuelPace.Core.Capture;

/// <summary>One lap as stored in a capture. Rounded: time to 3 places, liters and profile to 4, full throttle to 1.</summary>
public sealed class CapturedLap
{
    public int Lap { get; set; }
    /// <summary>Official (or measured fallback) lap time; null = no time.</summary>
    public double? TimeS { get; set; }
    public double FuelL { get; set; }
    public double FullThrottlePct { get; set; }
    public LapReasons Reasons { get; set; }
    /// <summary>101-point cumulative fuel fraction; null when the lap did not run line to line.</summary>
    public float[]? FuelProfile { get; set; }

    public bool Valid => Reasons == LapReasons.None;

    public static CapturedLap From(in CompletedLap l)
    {
        float[]? profile = null;
        if (l.HasProfile)
        {
            profile = new float[FuelProfile101.Length];
            for (int k = 0; k < profile.Length; k++)
                profile[k] = (float)Math.Round(l.FuelProfile[k], 4, MidpointRounding.AwayFromZero);
        }
        return new CapturedLap
        {
            Lap = l.LapNumber,
            TimeS = double.IsNaN(l.TimeS) ? null : Math.Round(l.TimeS, 3, MidpointRounding.AwayFromZero),
            FuelL = Math.Round(l.FuelUsedL, 4, MidpointRounding.AwayFromZero),
            FullThrottlePct = Math.Round(l.FullThrottlePct, 1, MidpointRounding.AwayFromZero),
            Reasons = l.Reasons,
            FuelProfile = profile,
        };
    }
}
