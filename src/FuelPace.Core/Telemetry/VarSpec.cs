namespace FuelPace.Core.Telemetry;

public readonly record struct VarSpec(string Name, VarType Type, int Count = 1, string Unit = "");
