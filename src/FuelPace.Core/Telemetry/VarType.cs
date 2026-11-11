namespace FuelPace.Core.Telemetry;

public enum VarType { Char = 0, Bool = 1, Int = 2, BitField = 3, Float = 4, Double = 5 }

public static class VarTypeExt
{
    /// <summary>Size in bytes of one element of the given type.</summary>
    public static int VarTypeSize(this VarType type) => type switch
    {
        VarType.Char or VarType.Bool => 1,
        VarType.Int or VarType.BitField or VarType.Float => 4,
        VarType.Double => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "unknown var type"),
    };
}
