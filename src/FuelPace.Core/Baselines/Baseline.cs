namespace FuelPace.Core.Baselines;

public enum Confidence { Baixa, Media, Alta }

/// <summary>Linear fit <c>time = A + B * fuel</c> (seconds, liters).</summary>
public sealed record FuelVsTime(double A, double B);

/// <summary>Car balance-of-performance values a capture was recorded under.</summary>
public sealed record BopSignature(double PowerAdjustPct, double WeightPenaltyKg, double TankCapacityL)
{
    public bool Matches(BopSignature other) =>
        Math.Abs(PowerAdjustPct - other.PowerAdjustPct) <= 0.01
        && Math.Abs(WeightPenaltyKg - other.WeightPenaltyKg) <= 0.1
        && Math.Abs(TankCapacityL - other.TankCapacityL) <= 0.1;
}

public sealed record Baseline(
    DateTimeOffset ComputedAt,
    int ValidLaps,
    double FuelMeanL, double FuelMedianL, double FuelStdL, double FuelMinL, double FuelP10L, double FuelP90L,
    double LapMeanS, double LapMedianS, double LapStdS, double LapBestS,
    double FullThrottleMeanPct,
    double AutonomyLaps, double AutonomyMin,
    FuelVsTime? FuelVsTime,
    Confidence Confidence,
    float[] FuelProfile,
    IReadOnlyList<BopSignature> Conditions);
