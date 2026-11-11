using System.Buffers.Binary;

namespace FuelPace.Core.Telemetry;

public readonly record struct VarBufInfo(int TickCount, int BufOffset);

public readonly record struct IrsdkHeader(
    int Ver, int Status, int TickRate, int SessionInfoUpdate, int SessionInfoLen, int SessionInfoOffset,
    int NumVars, int VarHeaderOffset, int NumBuf, int BufLen,
    VarBufInfo Buf0, VarBufInfo Buf1, VarBufInfo Buf2, VarBufInfo Buf3)
{
    public const int Size = 112;

    public static IrsdkHeader Parse(ReadOnlySpan<byte> first112Bytes)
    {
        if (first112Bytes.Length < Size)
            throw new ArgumentException("irsdk header needs 112 bytes", nameof(first112Bytes));
        var s = first112Bytes;
        return new IrsdkHeader(I(s, 0), I(s, 4), I(s, 8), I(s, 12), I(s, 16), I(s, 20), I(s, 24), I(s, 28), I(s, 32), I(s, 36),
            B(s, 48), B(s, 64), B(s, 80), B(s, 96));
    }

    private static int I(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt32LittleEndian(s[o..]);
    private static VarBufInfo B(ReadOnlySpan<byte> s, int o) => new(I(s, o), I(s, o + 4));
}
