using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Telemetry;

public class SessionInfoParserTests
{
    [Fact]
    public void Parses_fixture_like_yaml()
    {
        var s = SessionInfoParser.Parse(SampleYaml.Zolder);
        Assert.Equal(3, s.DriverCarIdx);
        Assert.Equal("ligierjsp320", s.CarPath);
        Assert.Equal("Ligier JS P320", s.CarScreenName);
        Assert.Equal("Ligier", s.CarScreenNameShort);
        Assert.Equal("Zolder", s.TrackDisplayShortName);
        Assert.Equal("zolder alt", s.TrackName);
        Assert.Equal("Zolder Circuit", s.TrackDisplayName);
        Assert.Equal("Alternate", s.TrackConfigName);
        Assert.Equal(3.9342, s.TrackLengthKm, 4);
        Assert.Equal(100.0, s.TankCapacityL, 3);
        Assert.Equal(0.75, s.FuelKgPerL, 3);
        Assert.Equal(78.9567, s.EstLapTimeS, 4);
        Assert.Equal(new SessionDef(0, "Practice", null, 7200), s.FindSession(0));
        Assert.Equal(new SessionDef(1, "Race", 25, null), s.FindSession(1));
        Assert.Null(s.FindSession(9));
        Assert.Equal("ligierjsp320__zolder alt", s.ProfileKey);
    }

    [Fact]
    public void Tank_capacity_applies_max_fuel_pct()
    {
        var s = SessionInfoParser.Parse(SampleYaml.WithMaxFuelPct("0.600"));
        Assert.Equal(60.0, s.TankCapacityL, 3);
    }

    [Fact]
    public void Sanitize_quotes_values_with_colons_hashes_and_quotes()
    {
        var s = SessionInfoParser.Parse(SampleYaml.Zolder);
        Assert.Equal("ligierjsp320", s.CarPath);
        var clean = SessionInfoParser.Sanitize(SampleYaml.Zolder);
        Assert.Contains("UserName: 'Joe: The \"Rocket\"'", clean);
        Assert.Contains("TeamName: 'Team #1'", clean);
        Assert.Equal("'it''s: x'", SessionInfoParser.Sanitize("A: it's: x").Substring(3));
    }

    [Fact]
    public void Crlf_yaml_parses_identically()
    {
        var a = SessionInfoParser.Parse(SampleYaml.Zolder);
        var b = SessionInfoParser.Parse(SampleYaml.Zolder.Replace("\n", "\r\n"));
        Assert.Equal(a.CarPath, b.CarPath);
        Assert.Equal(a.TrackName, b.TrackName);
        Assert.Equal(a.CarScreenNameShort, b.CarScreenNameShort);
        Assert.Equal(a.TankCapacityL, b.TankCapacityL);
        Assert.True(a.Sessions.SequenceEqual(b.Sessions));
    }

    [Fact]
    public void Missing_optional_fields_fall_back()
    {
        var yaml = SampleYaml.Zolder
            .Replace("   CarScreenNameShort: Ligier\n", "")
            .Replace(" TrackDisplayShortName: Zolder\n", "")
            .Replace(" DriverCarEstLapTime: 78.9567\n", "");
        var s = SessionInfoParser.Parse(yaml);
        Assert.Equal("Ligier JS P320", s.CarScreenNameShort);
        Assert.Equal("Zolder Circuit", s.TrackDisplayShortName);
        Assert.True(double.IsNaN(s.EstLapTimeS));
    }

    [Fact]
    public void Missing_sections_throw_format_exception()
    {
        Assert.Throws<FormatException>(() => SessionInfoParser.Parse("SessionInfo:\n Sessions:\n"));
        Assert.Throws<FormatException>(() => SessionInfoParser.Parse(""));
        Assert.Throws<FormatException>(() => SessionInfoParser.Parse("WeekendInfo:\n TrackName: x\n"));
    }

    [Fact]
    public void Fixture_yaml_parses()
    {
        using var src = new IbtTelemetrySource(Fixtures.RequireZolder());
        var s = SessionInfoParser.Parse(src.ReadSessionInfoYaml());
        Assert.Equal("ligierjsp320", s.CarPath);
        Assert.Equal("zolder alt", s.TrackName);
        Assert.Equal(100.0, s.TankCapacityL, 3);
        Assert.Equal(78.9567, s.EstLapTimeS, 4);
        Assert.False(string.IsNullOrEmpty(s.CarScreenNameShort));
        Assert.False(string.IsNullOrEmpty(s.TrackDisplayShortName));
        Assert.Equal(new SessionDef(0, "Practice", null, 7200), s.FindSession(0));
    }
}
