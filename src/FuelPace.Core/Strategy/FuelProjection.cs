namespace FuelPace.Core.Strategy;

/// <summary>Projects whole-lap fuel use from partial-lap use and a 101-point cumulative consumption profile. Allocation-free.</summary>
public static class FuelProjection
{
    /// <summary>Below this lap fraction the projection is hidden (NaN).</summary>
    public const double MinPct = 0.15;

    const int ProfileLength = 101;

    /// <summary>Linear interpolation of the profile at <paramref name="pct"/> in [0,1]; NaN outside it, for NaN or a wrong-length profile.</summary>
    public static double Interpolate(ReadOnlySpan<float> profile, double pct)
    {
        if (profile.Length != ProfileLength || !(pct >= 0 && pct <= 1)) return double.NaN;
        double pos = pct * (ProfileLength - 1);
        int i = (int)pos;
        if (i >= ProfileLength - 1) return profile[ProfileLength - 1];
        double frac = pos - i;
        return profile[i] + (profile[i + 1] - profile[i]) * frac;
    }

    /// <summary>
    /// Projected fuel for the whole lap. NaN when <paramref name="lapDistPct"/> is below <see cref="MinPct"/>, above 1 or NaN, or when
    /// the interpolated profile value is not positive. An empty (or wrong-length) profile falls back to <c>used / pct</c>.
    /// </summary>
    public static double Project(double usedSoFarL, double lapDistPct, ReadOnlySpan<float> refProfile)
    {
        if (!(lapDistPct >= MinPct && lapDistPct <= 1)) return double.NaN;
        if (refProfile.Length != ProfileLength) return usedSoFarL / lapDistPct;
        double f = Interpolate(refProfile, lapDistPct);
        return f > 0 ? usedSoFarL / f : double.NaN;
    }
}
