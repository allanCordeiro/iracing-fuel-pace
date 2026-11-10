using FuelPace.Agent.Live;
using FuelPace.Core.Telemetry;
using FuelPace.FakeSim;

namespace FuelPace.Windows.Tests;

public class FakeSimArgsTests
{
    private static FakeSimArgs Parse(params string[] args)
    {
        Assert.True(FakeSimArgs.TryParse(args, out var a, out var error), error);
        Assert.Null(error);
        return a;
    }

    private static string Fails(params string[] args)
    {
        Assert.False(FakeSimArgs.TryParse(args, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        return error;
    }

    [Fact]
    public void A_synthetic_race_uses_the_irsdk_names_and_the_defaults()
    {
        var a = Parse("--synthetic-race", "30");
        Assert.Equal(30, a.SyntheticLaps);
        Assert.Null(a.IbtPath);
        Assert.Equal(ReplaySpeed.X1, a.Speed);
        Assert.False(a.Loop);
        Assert.Equal(20, a.Cars);
        Assert.Null(a.DisconnectAfter);
        Assert.Equal(IRacingMemoryReader.DefaultMapName, a.MapName);   // the names the Agent opens by default
        Assert.Equal(IRacingMemoryReader.DefaultEventName, a.EventName);
    }

    [Fact]
    public void Every_option_is_read()
    {
        var a = Parse("--synthetic-race", "12", "--speed", "10", "--loop", "--cars", "64", "--disconnect-after", "7.5",
            "--map", @"Local\m", "--event", @"Local\e");
        Assert.Equal(12, a.SyntheticLaps);
        Assert.Equal(ReplaySpeed.X10, a.Speed);
        Assert.True(a.Loop);
        Assert.Equal(64, a.Cars);
        Assert.Equal(TimeSpan.FromSeconds(7.5), a.DisconnectAfter);
        Assert.Equal(@"Local\m", a.MapName);
        Assert.Equal(@"Local\e", a.EventName);
        Assert.Equal(ReplaySpeed.Max, Parse("--synthetic-race", "1", "--speed", "MAX").Speed);
        Assert.Equal(0, Parse("--synthetic-race", "1", "--cars", "0").Cars); // no CarIdx variables
    }

    [Fact]
    public void A_file_is_the_positional_argument()
    {
        var a = Parse(@"C:\races\zolder.ibt", "--speed", "max", "--loop");
        Assert.Equal(@"C:\races\zolder.ibt", a.IbtPath);
        Assert.Null(a.SyntheticLaps);
        Assert.Equal(ReplaySpeed.Max, a.Speed);
        Assert.True(a.Loop);
    }

    [Fact]
    public void A_source_is_required() => Assert.Contains("source", Fails(), StringComparison.Ordinal);

    [Theory]
    [InlineData("--loop")]
    [InlineData("a.ibt", "b.ibt")]
    [InlineData("a.ibt", "--synthetic-race", "3")]
    [InlineData("a.ibt", "--cars", "20")]
    [InlineData("--synthetic-race", "0")]
    [InlineData("--synthetic-race", "x")]
    [InlineData("--synthetic-race")]
    [InlineData("--synthetic-race", "3", "--speed", "5")]
    [InlineData("--synthetic-race", "3", "--cars", "65")]
    [InlineData("--synthetic-race", "3", "--cars", "-1")]
    [InlineData("--synthetic-race", "3", "--disconnect-after", "-1")]
    [InlineData("--synthetic-race", "3", "--disconnect-after", "NaN")]
    [InlineData("--synthetic-race", "3", "--map", "")]
    [InlineData("--synthetic-race", "3", "--bogus")]
    public void Bad_command_lines_are_refused(params string[] args) => Fails(args);
}
