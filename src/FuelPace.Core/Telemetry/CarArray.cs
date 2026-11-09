using System.Runtime.CompilerServices;

namespace FuelPace.Core.Telemetry;

/// <summary>Fixed-size per-car array (iRacing supports up to 64 cars) stored inline in the frame.</summary>
[InlineArray(Length)]
public struct CarArray<T> where T : unmanaged
{
    public const int Length = 64;
    private T _element0;
}
