using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FuelPace.Core.Telemetry;

public readonly record struct IbtDiskHeader(
    long SessionStartDate, double SessionStartTime, double SessionEndTime, int SessionLapCount, int RecordCount);

public sealed class IbtReader : IDisposable
{
    private const int DiskHeaderOffset = IrsdkHeader.Size;
    private const int DiskHeaderSize = 32;

    private readonly SafeFileHandle _handle;

    public IrsdkHeader Header { get; }
    public IbtDiskHeader DiskHeader { get; }
    public IReadOnlyList<VarHeader> Variables { get; }
    public string SessionInfoYaml { get; }
    public int RecordCount { get; }

    private IbtReader(SafeFileHandle handle, IrsdkHeader header, IbtDiskHeader disk,
        VarHeader[] vars, string yaml, int recordCount)
    {
        _handle = handle;
        Header = header;
        DiskHeader = disk;
        Variables = vars;
        SessionInfoYaml = yaml;
        RecordCount = recordCount;
    }

    public static IbtReader Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        try
        {
            long length = RandomAccess.GetLength(handle);
            Span<byte> head = stackalloc byte[DiskHeaderOffset + DiskHeaderSize];
            ReadExactly(handle, head, 0);
            var header = IrsdkHeader.Parse(head);
            var d = head[DiskHeaderOffset..];
            var disk = new IbtDiskHeader(
                BinaryPrimitives.ReadInt64LittleEndian(d),
                BinaryPrimitives.ReadDoubleLittleEndian(d[8..]),
                BinaryPrimitives.ReadDoubleLittleEndian(d[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(d[24..]),
                BinaryPrimitives.ReadInt32LittleEndian(d[28..]));

            if (header.NumVars < 0 || header.BufLen <= 0 || header.SessionInfoLen < 0
                || header.VarHeaderOffset < 0 || header.SessionInfoOffset < 0
                || (long)header.VarHeaderOffset + (long)header.NumVars * VarHeader.Size > length
                || (long)header.SessionInfoOffset + header.SessionInfoLen > length)
                throw new InvalidDataException("not a valid .ibt file: inconsistent header");

            var varBytes = new byte[header.NumVars * VarHeader.Size];
            ReadExactly(handle, varBytes, header.VarHeaderOffset);
            var vars = VarHeader.ParseAll(varBytes, header.NumVars);

            var yamlBytes = new byte[header.SessionInfoLen];
            ReadExactly(handle, yamlBytes, header.SessionInfoOffset);
            int nul = Array.IndexOf(yamlBytes, (byte)0);
            string yaml = Encoding.Latin1.GetString(yamlBytes, 0, nul >= 0 ? nul : yamlBytes.Length);

            int count = disk.RecordCount;
            if (count <= 0)
                count = (int)Math.Max(0, (length - header.Buf0.BufOffset) / header.BufLen);

            return new IbtReader(handle, header, disk, vars, yaml, count);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void ReadRecord(int index, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RecordCount);
        if (destination.Length < Header.BufLen)
            throw new ArgumentException("destination smaller than BufLen", nameof(destination));
        ReadExactly(_handle, destination[..Header.BufLen], Header.Buf0.BufOffset + (long)index * Header.BufLen);
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> dest, long offset)
    {
        while (!dest.IsEmpty)
        {
            int n = RandomAccess.Read(handle, dest, offset);
            if (n <= 0) throw new EndOfStreamException("unexpected end of .ibt file");
            dest = dest[n..];
            offset += n;
        }
    }

    public void Dispose() => _handle.Dispose();
}
