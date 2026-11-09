namespace FuelPace.Core.Baselines;

/// <summary>A valid lap as input to the baseline. <paramref name="FullThrottlePct"/> may be NaN; <paramref name="FuelProfile"/> may be null.</summary>
public readonly record struct LapSample(double FuelL, double TimeS, double FullThrottlePct, float[]? FuelProfile);
