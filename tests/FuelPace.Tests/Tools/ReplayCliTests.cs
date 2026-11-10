using FuelPace.Core.Storage;
using FuelPace.Replay;

namespace FuelPace.Tests.Tools;

public class ReplayCliTests
{
    [Fact]
    public void Prints_laps_and_summary_for_synthetic_file()
    {
        var path = Fixtures.TempPath(".ibt");
        var profiles = Fixtures.TempPath(".d");
        try
        {
            SyntheticRace.WriteIbt(path, 5, withCarIdx: false);
            var sw = new StringWriter();
            int code = ReplayCli.Run([path, "--capture", "--profiles", profiles], sw);
            var text = sw.ToString();
            Assert.True(code == 0, text);
            Assert.Contains("lap 2", text);
            Assert.Contains("✓", text);
            Assert.Contains("ALVO", text);
            Assert.Contains("Baseline", text);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(profiles, "profiles"), "*.json"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Capture_without_profiles_writes_to_a_temporary_root_and_prints_it()
    {
        var path = Fixtures.TempPath(".ibt");
        string? root = null;
        try
        {
            SyntheticRace.WriteIbt(path, 5, withCarIdx: false);
            var sw = new StringWriter();
            Assert.Equal(0, ReplayCli.Run([path, "--capture"], sw));
            var line = sw.ToString().Split('\n').Select(l => l.TrimEnd('\r'))
                .Single(l => l.StartsWith(ReplayCli.TempProfilesPrefix, StringComparison.Ordinal));
            root = line[ReplayCli.TempProfilesPrefix.Length..];
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            Assert.NotEqual(Path.GetFullPath(ProfileStore.DefaultRoot), Path.GetFullPath(root));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "profiles"), "*.json"));
        }
        finally
        {
            File.Delete(path);
            if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Marks_invalid_laps_with_reasons()
    {
        var path = Fixtures.TempPath(".ibt");
        try
        {
            SyntheticRace.WriteIbt(path, 6, withCarIdx: true);
            var sw = new StringWriter();
            Assert.Equal(0, ReplayCli.Run([path], sw));
            Assert.Contains("✗ incident", sw.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_file_returns_2()
    {
        var sw = new StringWriter();
        Assert.Equal(2, ReplayCli.Run([Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".ibt")], sw));
    }

    [Theory]
    [InlineData("--speed", "7")]
    [InlineData("--bogus")]
    [InlineData("--profiles")]
    public void Invalid_arguments_return_2(params string[] extra)
    {
        var path = Fixtures.TempPath(".ibt");
        try
        {
            SyntheticRace.WriteIbt(path, 2, withCarIdx: false);
            Assert.Equal(2, ReplayCli.Run([path, .. extra], new StringWriter()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void No_arguments_returns_2() => Assert.Equal(2, ReplayCli.Run([], new StringWriter()));

    [Fact]
    public void Zolder_fixture_with_capture()
    {
        var path = Fixtures.RequireZolder();
        var profiles = Fixtures.TempPath(".d");
        var sw = new StringWriter();
        Assert.Equal(0, ReplayCli.Run([path, "--capture", "--profiles", profiles], sw));
        var text = sw.ToString();
        Assert.Contains("lap 16", text);
        Assert.Contains("Baseline", text);
    }
}
