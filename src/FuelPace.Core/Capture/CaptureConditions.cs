namespace FuelPace.Core.Capture;

/// <summary>Conditions recorded with a capture (CAP-09).</summary>
public sealed record CaptureConditions(
    double TrackTempC,
    double AirTempC,
    int Wetness,
    double PowerAdjustPct,
    double WeightPenaltyKg,
    double TankCapacityL);
