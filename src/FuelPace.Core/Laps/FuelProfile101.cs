using System.Runtime.CompilerServices;

namespace FuelPace.Core.Laps;

/// <summary>Cumulative fuel fraction (0..1) at every 1% of the lap: index k = LapDistPct k/100.</summary>
[InlineArray(Length)]
public struct FuelProfile101
{
    public const int Length = 101;
    private float _e;
}
