using System.IO.Compression;
using System.Text;
using FuelPace.Replay;

namespace FuelPace.Tests.Tools;

public class DiagCliTests
{
    private static string Line(string t, int seq, string lvl, string rest) =>
        $"{{\"t\":\"{t}\",\"seq\":{seq},\"lvl\":\"{lvl}\",{rest}}}";

    private static string SyntheticLog(bool shuffleAcrossFiles = false) => string.Join('\n',
    [
        Line("2026-11-09T14:00:00.000Z", 1, "info", "\"cat\":\"app\",\"msg\":\"startup 0.1.0+test\""),
        Line("2026-11-09T14:00:10.000Z", 2, "info", "\"st\":10,\"cat\":\"strategy\",\"ev\":\"StateChanged\",\"raw\":1,\"displayed\":1"),
        Line("2026-11-09T14:00:20.000Z", 3, "info", "\"st\":20,\"cat\":\"laps\",\"ev\":\"LapCompleted\",\"lap\":2,\"reasons\":[],\"timeS\":90.5"),
        Line("2026-11-09T14:00:50.000Z", 4, "info", "\"st\":50,\"cat\":\"user\",\"ev\":\"UserMark\",\"markNumber\":1"),
        Line("2026-11-09T14:00:55.000Z", 5, "error", "\"cat\":\"app\",\"msg\":\"System.InvalidOperationException: boom\\n   at Foo.Bar() in X.cs:line 12\""),
        Line("2026-11-09T14:05:00.000Z", 9, "info", "\"st\":300,\"cat\":\"laps\",\"ev\":\"LapCompleted\",\"lap\":9,\"reasons\":[],\"timeS\":91"),
        Line("2026-11-09T14:01:00.000Z", 6, "info", "\"st\":60,\"cat\":\"perf\",\"ev\":\"PerfMinute\",\"gen0Delta\":3,\"publishes\":600,\"tickP99Us\":42,\"tickMaxUs\":900,\"workingSetMB\":120,\"droppedDiag\":0"),
        Line("2026-11-09T14:01:01.000Z", 7, "info", "\"cat\":\"overlay\",\"ev\":\"OverlayRedraws\",\"perMinute\":412"),
        "{\"t\":\"2026-11-09T14:0", // truncated by a crash
    ]);

    private static string Run(string path, out int code)
    {
        var sw = new StringWriter();
        code = ReplayCli.Run(["diag", path], sw);
        return sw.ToString();
    }

    [Fact]
    public void Diag_cli_summarizes_marks_and_errors()
    {
        var dir = Fixtures.TempPath(".d");
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "logs", "fuelpace-20261109-1.jsonl"), SyntheticLog());
        File.WriteAllText(Path.Combine(dir, "system-info.txt"), "version: 0.1.0+test\nos: Windows 11\n");
        try
        {
            var text = Run(dir, out int code);
            Assert.Equal(0, code);
            Assert.Contains("0.1.0+test", text);
            Assert.Contains("Windows 11", text);
            Assert.Contains("Marcador 1", text);
            Assert.Contains("InvalidOperationException: boom", text);
            Assert.Contains("at Foo.Bar() in X.cs:line 12", text);
            Assert.Contains("PerfMinute", text);
            Assert.Contains("42", text);
            Assert.Contains("900", text);
            Assert.Contains("412", text);
            Assert.Contains("1 linha(s) inválida(s)", text);
            // mark at 14:00:50: +-30 s window includes StateChanged@14:00:20? (30 s earlier, inclusive) and the error, not the 14:05 lap
            var markSection = text[text.IndexOf("Marcador 1", StringComparison.Ordinal)..];
            Assert.Contains("LapCompleted", markSection);
            Assert.Contains("boom", markSection);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Diag_cli_orders_events_by_seq_across_files_and_reads_zip()
    {
        var zip = Fixtures.TempPath(".zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, string body)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(body);
            }
            // newer file listed first, older second: order must come from seq
            Add("logs/fuelpace-20261109-2.jsonl", Line("2026-11-09T14:10:00.000Z", 20, "info", "\"cat\":\"laps\",\"ev\":\"LapCompleted\",\"lap\":5,\"reasons\":[],\"timeS\":80") + "\n");
            Add("logs/fuelpace-20261109-1.jsonl", Line("2026-11-09T14:00:00.000Z", 10, "info", "\"cat\":\"laps\",\"ev\":\"LapCompleted\",\"lap\":4,\"reasons\":[],\"timeS\":81") + "\n");
            Add("system-info.txt", "version: 9.9.9\n");
        }
        try
        {
            var text = Run(zip, out int code);
            Assert.Equal(0, code);
            Assert.Contains("9.9.9", text);
            Assert.True(text.IndexOf("lap 4", StringComparison.Ordinal) is var a and >= 0 && text.IndexOf("lap 5", StringComparison.Ordinal) > a, text);
        }
        finally { File.Delete(zip); }
    }

    [Fact]
    public void Diag_cli_keeps_runs_apart_when_seq_restarts()
    {
        var dir = Fixtures.TempPath(".d");
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "logs", "fuelpace-20261109-1.jsonl"),
            Line("2026-11-09T14:00:00.000Z", 500, "info", "\"cat\":\"laps\",\"ev\":\"LapCompleted\",\"lap\":4,\"reasons\":[],\"timeS\":81") + "\n" +
            Line("2026-11-09T15:00:00.000Z", 1, "info", "\"cat\":\"laps\",\"ev\":\"LapCompleted\",\"lap\":5,\"reasons\":[],\"timeS\":80") + "\n");
        try
        {
            var text = Run(dir, out _);
            Assert.True(text.IndexOf("lap 4", StringComparison.Ordinal) is var a and >= 0 && text.IndexOf("lap 5", StringComparison.Ordinal) > a, text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Diag_cli_lists_recordings_with_duration_and_laps()
    {
        var dir = Fixtures.TempPath(".d");
        Directory.CreateDirectory(Path.Combine(dir, "recordings"));
        SyntheticRace.WriteIbt(Path.Combine(dir, "recordings", "r1.ibt"), 3, withCarIdx: false);
        try
        {
            var text = Run(dir, out int code);
            Assert.Equal(0, code);
            Assert.Contains("r1.ibt", text);
            Assert.Contains("60.0 s, 4 voltas detectadas", text); // 3 synthetic laps: the detector also closes the lap the file starts in
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Diag_cli_rejects_missing_path()
    {
        var sw = new StringWriter();
        Assert.Equal(2, ReplayCli.Run(["diag", "/nonexistent/x.zip"], sw));
        Assert.Equal(2, ReplayCli.Run(["diag"], sw));
    }
}
