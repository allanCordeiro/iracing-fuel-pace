using System.Buffers.Binary;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Telemetry;

public class IbtReaderTests
{
    private static readonly VarSpec[] Vars =
    [
        new("SessionTime", VarType.Double, 1, "s"), new("Lap", VarType.Int), new("LapDistPct", VarType.Float, 1, "%")
    ];

    [Fact]
    public void Reads_header_vars_yaml_and_records_from_synthetic_file()
    {
        var path = Fixtures.TempPath(".ibt");
        var yaml = "WeekendInfo:\n TrackName: test track\n";
        IbtFileBuilder.Write(path, yaml, Vars, [[10.0, 3, 0.25], [10.5, 3, 0.5], [11.0, 4, 0.01]]);

        using var r = IbtReader.Open(path);
        Assert.Equal(60, r.Header.TickRate);
        Assert.Equal(3, r.RecordCount);
        Assert.Equal(["SessionTime", "Lap", "LapDistPct"], r.Variables.Select(v => v.Name));
        Assert.Equal("s", r.Variables[0].Unit);
        Assert.Equal(yaml, r.SessionInfoYaml);

        var row = new byte[r.Header.BufLen];
        r.ReadRecord(2, row);
        Assert.Equal(11.0, BinaryPrimitives.ReadDoubleLittleEndian(row.AsSpan(r.Variables[0].Offset)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(r.Variables[1].Offset)));
    }

    [Fact]
    public void ReadRecord_out_of_range_throws()
    {
        var path = Fixtures.TempPath(".ibt");
        IbtFileBuilder.Write(path, "a: b\n", Vars, [[1.0, 1, 0.1], [2.0, 1, 0.2]]);
        using var r = IbtReader.Open(path);
        var row = new byte[r.Header.BufLen];
        Assert.Throws<ArgumentOutOfRangeException>(() => r.ReadRecord(r.RecordCount, row));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.ReadRecord(-1, row));
    }

    [Fact]
    public void Zero_record_count_is_derived_from_file_length()
    {
        var path = Fixtures.TempPath(".ibt");
        IbtFileBuilder.Write(path, "a: b\n", Vars, [[1.0, 1, 0.1], [2.0, 1, 0.2], [3.0, 2, 0.3]]);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(112 + 28), 0); // RecordCount = 0 (crashed recording)
        File.WriteAllBytes(path, [.. bytes, .. new byte[7]]);               // partial trailing row

        using var r = IbtReader.Open(path);
        Assert.Equal(3, r.RecordCount);
        Assert.Equal(3, (new FileInfo(path).Length - r.Header.Buf0.BufOffset) / r.Header.BufLen);
    }

    [Fact]
    public void Fixture_has_expected_shape()
    {
        using var r = IbtReader.Open(Fixtures.RequireZolder());
        Assert.Equal(60, r.Header.TickRate);
        Assert.Equal(47106, r.RecordCount);
        Assert.Contains(r.Variables, v => v.Name == "FuelLevel" && v.Type == VarType.Float);
        Assert.Contains(r.Variables, v => v.Name == "SessionTime" && v.Type == VarType.Double);
        Assert.DoesNotContain(r.Variables, v => v.Name.StartsWith("CarIdx"));
        Assert.Contains("CarPath: ligierjsp320", r.SessionInfoYaml);
    }
}
