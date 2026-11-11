using System.Globalization;
using System.Text;
using FuelPace.Replay;

namespace FuelPace.Tests.Tools;

public class PresentMonCliTests
{
    private static string Csv(string column, IEnumerable<double> frameTimesMs, bool extraColumns = true)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(extraColumns ? $"Application,ProcessID,{column},Dropped" : column);
        foreach (var ft in frameTimesMs)
            sb.AppendLine(extraColumns ? $"iRacingSim64DX11.exe,4242,{ft.ToString("F4", inv)},0" : ft.ToString("F4", inv));
        var path = Fixtures.TempPath(".csv");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static IEnumerable<double> Steady(double fps, int n) => Enumerable.Repeat(1000.0 / fps, n);

    private static string Run(string with, string without, out int code)
    {
        var sw = new StringWriter();
        code = ReplayCli.Run(["presentmon", with, without], sw);
        return sw.ToString();
    }

    [Fact]
    public void Fails_perf01_when_average_drops_and_spikes_appear()
    {
        // ~59.5 fps average (a 60 ms spike every 50 frames, the rest slightly faster) vs. a steady 60 fps
        var withApp = Enumerable.Range(0, 3000).Select(i => i % 50 == 0 ? 60.0 : 1000.0 / 60.0 * 0.99).ToList();
        string a = Csv("MsBetweenPresents", withApp), b = Csv("MsBetweenPresents", Steady(60, 3000));
        try
        {
            var text = Run(a, b, out int code);
            Assert.Equal(1, code);
            Assert.Contains("fps médio", text);
            Assert.Contains("1% low", text);
            Assert.Contains("> 2×", text);
            Assert.Contains("PERF-01: REPROVADO", text);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void Passes_perf01_for_equivalent_runs_and_reads_the_PresentMon_2_FrameTime_column()
    {
        string a = Csv("FrameTime", Steady(60, 3000)), b = Csv("FrameTime", Steady(60, 3000));
        try
        {
            var text = Run(a, b, out int code);
            Assert.Equal(0, code);
            Assert.Contains("fps médio", text);
            Assert.Contains("60.0", text);
            Assert.Contains("PERF-01: APROVADO", text);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void One_percent_low_is_the_fps_of_the_slowest_one_percent_of_frames()
    {
        // 99 frames at 10 ms + 1 frame at 110 ms: mean 11 ms; 1% low = the single 110 ms frame = 9.1 fps
        var times = Steady(100, 99).Append(110.0);
        string a = Csv("MsBetweenPresents", times), b = Csv("MsBetweenPresents", Steady(100, 100));
        try
        {
            var text = Run(a, b, out _);
            Assert.Contains("9.1", text);
            Assert.Contains("PERF-01: REPROVADO", text);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void Compares_spike_rates_not_counts_and_matches_columns_ignoring_case()
    {
        // with: 2 spikes in 6000 frames (0.33/1000); without: 1 in 1000 (1/1000) -> rate did not increase
        var with = Steady(60, 5998).Concat([100.0, 100.0]);
        var without = Steady(60, 999).Concat([100.0]);
        string a = Csv("frametime", with), b = Csv("FRAMETIME", without);
        try
        {
            var text = Run(a, b, out int code);
            Assert.Contains("PERF-01: APROVADO", text);
            Assert.Equal(0, code);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    [Fact]
    public void Reports_a_missing_column_or_file_with_exit_code_2()
    {
        string a = Csv("Foo", Steady(60, 10)), b = Csv("FrameTime", Steady(60, 10));
        try
        {
            Assert.Equal(2, ReplayCli.Run(["presentmon", a, b], new StringWriter()));
            Assert.Equal(2, ReplayCli.Run(["presentmon", a, "/nonexistent.csv"], new StringWriter()));
            Assert.Equal(2, ReplayCli.Run(["presentmon", a], new StringWriter()));
        }
        finally { File.Delete(a); File.Delete(b); }
    }
}
