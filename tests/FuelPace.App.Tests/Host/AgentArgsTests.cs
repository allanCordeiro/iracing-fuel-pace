using FuelPace.App.Host;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Tests.Host;

public class AgentArgsTests
{
    [Fact]
    public void No_arguments_is_the_live_agent()
    {
        Assert.True(AgentArgs.TryParse([], out var a, out var error));
        Assert.Null(error);
        Assert.Equal(new AgentArgs(false, null, ReplaySpeed.X1, null, null), a);
    }

    [Fact]
    public void Every_option_is_parsed()
    {
        Assert.True(AgentArgs.TryParse(
            ["--debug", "--replay", "C:\\rec\\a b.ibt", "--speed", "max", "--exit-after", "2.5", "--appdata", "D:\\tmp\\fp"],
            out var a, out _));
        Assert.True(a.Debug);
        Assert.Equal("C:\\rec\\a b.ibt", a.ReplayPath);
        Assert.Equal(ReplaySpeed.Max, a.Speed);
        Assert.Equal(TimeSpan.FromSeconds(2.5), a.ExitAfter);
        Assert.Equal("D:\\tmp\\fp", a.AppData);
    }

    [Theory]
    [InlineData("1", ReplaySpeed.X1)]
    [InlineData("10", ReplaySpeed.X10)]
    [InlineData("MAX", ReplaySpeed.Max)]
    public void Speeds(string text, ReplaySpeed expected)
    {
        Assert.True(AgentArgs.TryParse(["--speed", text], out var a, out _));
        Assert.Equal(expected, a.Speed);
    }

    [Theory]
    [InlineData("--speed", "2")]
    [InlineData("--exit-after", "abc")]
    [InlineData("--exit-after", "-1")]
    [InlineData("--exit-after", "NaN")]
    [InlineData("--bogus", null)]
    [InlineData("--replay", null)]
    [InlineData("--appdata", "")]
    public void Invalid_arguments_are_rejected_with_a_reason(string option, string? value)
    {
        string[] args = value is null ? [option] : [option, value];
        Assert.False(AgentArgs.TryParse(args, out _, out var error));
        Assert.Contains(option, error);
    }

    [Fact]
    public void Exit_after_uses_the_invariant_culture()
    {
        Assert.False(AgentArgs.TryParse(["--exit-after", "1,5"], out _, out _));
        Assert.True(AgentArgs.TryParse(["--exit-after", "40"], out var a, out _));
        Assert.Equal(TimeSpan.FromSeconds(40), a.ExitAfter);
    }

    [Fact]
    public void Options_use_the_default_app_data_unless_given()
    {
        AgentArgs.TryParse(["--debug", "--replay", "x.ibt", "--speed", "10"], out var a, out _);
        var o = a.ToOptions(defaultAppData: "C:\\AppData\\FuelPace", debugMarker: true);
        Assert.Equal(new AgentOptions("C:\\AppData\\FuelPace", true, true, "x.ibt", ReplaySpeed.X10, null), o);

        AgentArgs.TryParse(["--appdata", "E:\\fp"], out var b, out _);
        Assert.Equal("E:\\fp", b.ToOptions("C:\\AppData\\FuelPace", false).AppDataRoot);
    }
}
