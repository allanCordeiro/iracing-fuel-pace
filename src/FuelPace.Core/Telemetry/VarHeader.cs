using System.Buffers.Binary;
using System.Text;

namespace FuelPace.Core.Telemetry;

public sealed record VarHeader(VarType Type, int Offset, int Count, bool CountAsTime, string Name, string Desc, string Unit)
{
    public const int Size = 144;

    public static VarHeader[] ParseAll(ReadOnlySpan<byte> src, int count)
    {
        if (count < 0 || src.Length < (long)count * Size)
            throw new ArgumentException("buffer too small for var headers", nameof(src));
        var result = new VarHeader[count];
        for (int i = 0; i < count; i++)
        {
            var h = src.Slice(i * Size, Size);
            result[i] = new VarHeader(
                (VarType)BinaryPrimitives.ReadInt32LittleEndian(h),
                BinaryPrimitives.ReadInt32LittleEndian(h[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(h[8..]),
                h[12] != 0,
                CString(h.Slice(16, 32)),
                CString(h.Slice(48, 64)),
                CString(h.Slice(112, 32)));
        }
        return result;
    }

    private static string CString(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        return Encoding.Latin1.GetString(n >= 0 ? s[..n] : s);
    }
}
