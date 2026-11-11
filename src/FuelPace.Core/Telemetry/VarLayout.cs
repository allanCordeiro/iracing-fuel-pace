namespace FuelPace.Core.Telemetry;

/// <summary>Where a variable lives inside a record. <c>Offset = -1</c> means the variable is absent.</summary>
public readonly record struct VarSlot(int Offset, VarType Type, int Count)
{
    public static VarSlot Absent => new(-1, VarType.Char, 0);
}

/// <summary>Maps <see cref="TelemetryVarId"/> to record offsets by iRacing variable name.</summary>
public sealed class VarLayout
{
    private readonly VarSlot[] _slots;

    private VarLayout(VarSlot[] slots, string fuelUnit, bool hasCarIdx, int missing)
    {
        _slots = slots;
        FuelUnit = fuelUnit;
        HasCarIdx = hasCarIdx;
        MissingCount = missing;
    }

    public VarSlot this[TelemetryVarId id] => _slots[(int)id];
    public bool Has(TelemetryVarId id) => _slots[(int)id].Offset >= 0;

    /// <summary>True when every CarIdx* variable is present.</summary>
    public bool HasCarIdx { get; }
    /// <summary>Number of catalog variables absent from the source.</summary>
    public int MissingCount { get; }
    public string FuelUnit { get; }
    public bool IsElectric => string.Equals(FuelUnit, "kWh", StringComparison.OrdinalIgnoreCase);

    public static VarLayout Resolve(IReadOnlyList<VarHeader> headers)
    {
        var byName = new Dictionary<string, VarHeader>(headers.Count, StringComparer.Ordinal);
        foreach (var h in headers) byName.TryAdd(h.Name, h);

        var all = TelemetryVars.All;
        var slots = new VarSlot[all.Count];
        bool hasCarIdx = true;
        int missing = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            var spec = all[i];
            if (byName.TryGetValue(spec.Name, out var h) && h.Count >= 1)
                slots[i] = new VarSlot(h.Offset, h.Type, h.Count);
            else
            {
                slots[i] = VarSlot.Absent;
                missing++;
                if (TelemetryVars.IsCarIdx((TelemetryVarId)i)) hasCarIdx = false;
            }
        }
        string unit = byName.TryGetValue("FuelLevel", out var fh) ? fh.Unit : "";
        return new VarLayout(slots, unit, hasCarIdx, missing);
    }
}
