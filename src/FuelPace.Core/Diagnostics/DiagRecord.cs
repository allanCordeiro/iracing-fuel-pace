namespace FuelPace.Core.Diagnostics;

/// <summary>Fixed-size, string-free diagnostic record.</summary>
public struct DiagRecord
{
    public long Seq;
    public long WallTicksUtc;
    public double SessionTime;
    public DiagEvent Event;
    public DiagLevel Level;
    public int I0, I1;
    public double A, B, C, D;
}

public readonly record struct DiagText(long Seq, long WallTicksUtc, DiagLevel Level, string Category, string Message);
