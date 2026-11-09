namespace FuelPace.Core.Baselines;

/// <summary>§6.4: aggregates over the valid laps. Pure; not on the hot path.</summary>
public static class BaselineCalculator
{
    public const int MinLaps = 3;
    const int ProfileLength = 101;
    /// <summary>The regression (PRD §6.4) needs at least this many valid laps,</summary>
    const int MinRegressionLaps = 8;
    /// <summary>a fuel range of at least this fraction of the median,</summary>
    const double MinRangeFraction = 0.02;
    /// <summary>and at least this many laps below median − 1 standard deviation.</summary>
    const int MinEconomyLaps = 2;

    public static Baseline? Compute(IReadOnlyList<LapSample> validLaps, double tankCapacityL,
        IReadOnlyList<BopSignature> conditions, DateTimeOffset computedAt)
    {
        int n = validLaps.Count;
        if (n < MinLaps) return null;

        var fuel = validLaps.Select(l => l.FuelL).OrderBy(x => x).ToArray();
        var time = validLaps.Select(l => l.TimeS).OrderBy(x => x).ToArray();
        double fuelMean = fuel.Average(), lapMean = time.Average();
        double fuelMedian = Percentile(fuel, 0.5);

        var throttle = validLaps.Select(l => l.FullThrottlePct).Where(x => !double.IsNaN(x)).ToArray();
        double fuelStd = Std(fuel, fuelMean);
        double autonomyLaps = tankCapacityL / fuelMean;

        return new Baseline(
            computedAt, n,
            fuelMean, fuelMedian, fuelStd, fuel[0], Percentile(fuel, 0.10), Percentile(fuel, 0.90),
            lapMean, Percentile(time, 0.5), Std(time, lapMean), time[0],
            throttle.Length == 0 ? double.NaN : throttle.Average(),
            autonomyLaps, autonomyLaps * lapMean / 60.0,
            Regress(validLaps, fuel, fuelMedian, fuelStd),
            n >= 10 ? Confidence.Alta : n >= 5 ? Confidence.Media : Confidence.Baixa,
            MeanProfile(validLaps.Where(l => l.FuelProfile is not null).Select(l => l.FuelProfile!).ToList()),
            conditions);
    }

    /// <summary>Pointwise mean of the 101-point profiles; null or wrong-length ones are ignored; none → empty array.</summary>
    public static float[] MeanProfile(IReadOnlyList<float[]> profiles)
    {
        var ok = profiles.Where(p => p is { Length: ProfileLength }).ToList();
        if (ok.Count == 0) return [];
        var mean = new float[ProfileLength];
        for (int k = 0; k < ProfileLength; k++)
        {
            double s = 0;
            foreach (var p in ok) s += p[k];
            mean[k] = (float)(s / ok.Count);
        }
        return mean;
    }

    static FuelVsTime? Regress(IReadOnlyList<LapSample> laps, double[] sortedFuel, double median, double std)
    {
        if (laps.Count < MinRegressionLaps) return null;
        if (median <= 0) return null;
        if ((sortedFuel[^1] - sortedFuel[0]) / median < MinRangeFraction) return null;
        if (sortedFuel.Count(f => f < median - std) < MinEconomyLaps) return null;

        double mx = laps.Average(l => l.FuelL), my = laps.Average(l => l.TimeS);
        double sxx = 0, sxy = 0;
        foreach (var l in laps)
        {
            sxx += (l.FuelL - mx) * (l.FuelL - mx);
            sxy += (l.FuelL - mx) * (l.TimeS - my);
        }
        if (sxx <= 0) return null;
        double b = sxy / sxx;
        return new FuelVsTime(my - b * mx, b);
    }

    /// <summary>rank = p × (n − 1), linear interpolation between neighbours. Input sorted ascending.</summary>
    static double Percentile(double[] sorted, double p)
    {
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (rank - lo) * (sorted[hi] - sorted[lo]);
    }

    /// <summary>Sample standard deviation (n − 1).</summary>
    static double Std(double[] v, double mean)
    {
        double s = 0;
        foreach (var x in v) s += (x - mean) * (x - mean);
        return Math.Sqrt(s / (v.Length - 1));
    }
}
