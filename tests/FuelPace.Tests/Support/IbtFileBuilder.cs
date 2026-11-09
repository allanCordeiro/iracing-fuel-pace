using System.Buffers.Binary;
using System.Text;
using FuelPace.Core.Telemetry;

namespace FuelPace.Tests.Support;

public static class IbtFileBuilder
{
    public static void Write(string path, string yaml, IReadOnlyList<VarSpec> vars, IEnumerable<double[]> rows, int tickRate = 60)
    {
        var yamlBytes = Encoding.Latin1.GetBytes(yaml + "\0");
        const int varHeaderSize = 144;
        int varHeaderOffset = 144;
        int yamlOffset = varHeaderOffset + vars.Count * varHeaderSize;
        int bufOffset = yamlOffset + yamlBytes.Length;

        var offsets = new int[vars.Count];
        int bufLen = 0;
        for (int i = 0; i < vars.Count; i++)
        {
            offsets[i] = bufLen;
            bufLen += VarTypeExt.VarTypeSize(vars[i].Type) * vars[i].Count;
        }

        var rowList = rows.ToList();
        var buf = new byte[bufOffset + rowList.Count * bufLen];
        var s = buf.AsSpan();

        // irsdk header (112 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(s[0..], 2);               // ver
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 1);               // status
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], tickRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], 0);              // sessionInfoUpdate
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], yamlBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], yamlOffset);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], vars.Count);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], varHeaderOffset);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], 1);              // numBuf
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], bufLen);
        BinaryPrimitives.WriteInt32LittleEndian(s[48..], 0);              // buf0.tickCount
        BinaryPrimitives.WriteInt32LittleEndian(s[52..], bufOffset);      // buf0.bufOffset

        // disk sub-header (32 bytes at 112)
        BinaryPrimitives.WriteInt64LittleEndian(s[112..], 0);
        BinaryPrimitives.WriteDoubleLittleEndian(s[120..], 0);
        BinaryPrimitives.WriteDoubleLittleEndian(s[128..], rowList.Count / (double)tickRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[136..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(s[140..], rowList.Count);

        for (int i = 0; i < vars.Count; i++)
        {
            var h = s[(varHeaderOffset + i * varHeaderSize)..];
            BinaryPrimitives.WriteInt32LittleEndian(h[0..], (int)vars[i].Type);
            BinaryPrimitives.WriteInt32LittleEndian(h[4..], offsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..], vars[i].Count);
            Encoding.Latin1.GetBytes(vars[i].Name, h.Slice(16, 32));
            Encoding.Latin1.GetBytes(vars[i].Unit, h.Slice(112, 32));
        }

        yamlBytes.CopyTo(s[yamlOffset..]);

        for (int r = 0; r < rowList.Count; r++)
        {
            var row = s[(bufOffset + r * bufLen)..];
            int e = 0;
            for (int i = 0; i < vars.Count; i++)
            {
                int size = VarTypeExt.VarTypeSize(vars[i].Type);
                for (int k = 0; k < vars[i].Count; k++, e++)
                {
                    var dst = row[(offsets[i] + k * size)..];
                    double v = rowList[r][e];
                    switch (vars[i].Type)
                    {
                        case VarType.Char: case VarType.Bool: dst[0] = (byte)v; break;
                        case VarType.Int: case VarType.BitField: BinaryPrimitives.WriteInt32LittleEndian(dst, (int)v); break;
                        case VarType.Float: BinaryPrimitives.WriteSingleLittleEndian(dst, (float)v); break;
                        case VarType.Double: BinaryPrimitives.WriteDoubleLittleEndian(dst, v); break;
                    }
                }
            }
        }

        File.WriteAllBytes(path, buf);
    }
}
