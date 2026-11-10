using System.IO.Compression;
using System.Text.Json;
using FuelPace.App.Diagnostics;
using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Tests.Diagnostics;

public sealed class DiagnosticBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuelpace-bundle-" + Guid.NewGuid().ToString("N"));
    private string Dest => Path.Combine(_root, "desktop");
    private string Data => Path.Combine(_root, "data");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 11, 9, 14, 5, 9, TimeSpan.Zero));
    private static readonly Dictionary<string, string> Info = new()
    {
        [SystemInfoKeys.Version] = "0.1.0+test",
        [SystemInfoKeys.Os] = "Windows 11",
        [SystemInfoKeys.Monitors] = "A 0,0,1920,1080 dpi96\nB 1920,0,1920,1080 dpi144",
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Put(string rel, string content, DateTime? utc = null)
    {
        var p = Path.Combine(Data, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        if (utc is { } t) File.SetLastWriteTimeUtc(p, t);
    }

    private static string[] Names(string zip)
    {
        using var z = ZipFile.OpenRead(zip);
        return z.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static string ReadEntry(string zip, string name)
    {
        using var z = ZipFile.OpenRead(zip);
        using var sr = new StreamReader(z.GetEntry(name)!.Open());
        return sr.ReadToEnd();
    }

    private void Populate()
    {
        Put("logs/fuelpace-20261109-1.jsonl",
            "{\"t\":\"2026-11-09T14:00:00.000Z\",\"seq\":1,\"lvl\":\"info\",\"ev\":\"UserMark\",\"markNumber\":1}\n" +
            "{\"t\":\"2026-11-09T14:00:05.000Z\",\"seq\":2,\"lvl\":\"info\",\"ev\":\"UserMark\",\"markNumber\":2}\n" +
            "{\"t\":\"2026-11-09T14:0");
        Put("settings.json", "{}");
        Put("settings.json.corrupt", "garbage");
        Put("profiles/a__b.json", "{}");
        Put("profiles/a__b.pending-x.json", "{}");
        Put("profiles/c__d.json.corrupt", "bad");
        Put("recordings/old.ibt", "old", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        Put("recordings/mid.ibt", "mid", new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));
        Put("recordings/new.ibt", "new", new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc));
        Put("perf/presentmon.csv", "a,b");
    }

    [Fact]
    public void Bundle_contains_expected_entries()
    {
        Populate();
        int flushed = 0;
        var zip = DiagnosticBundle.Create(Data, Dest, new BundleOptions(), Info, DiagLog.Null, Clock, () => flushed++, cancel: TestContext.Current.CancellationToken);

        Assert.Equal(1, flushed);
        Assert.Equal("FuelPace-diag-20261109-140509.zip", Path.GetFileName(zip));
        Assert.Equal(
        [
            "logs/fuelpace-20261109-1.jsonl", "manifest.json", "perf/presentmon.csv",
            "profiles/a__b.json", "profiles/a__b.pending-x.json", "profiles/c__d.json.corrupt",
            "recordings/mid.ibt", "recordings/new.ibt", "settings.json", "settings.json.corrupt", "system-info.txt",
        ], Names(zip));

        var sys = ReadEntry(zip, "system-info.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("version: 0.1.0+test", sys);
        Assert.Contains(sys, l => l.StartsWith("monitors: A 0,0,1920,1080 dpi96", StringComparison.Ordinal) && l.Contains("dpi144"));

        using var m = JsonDocument.Parse(ReadEntry(zip, "manifest.json"));
        var root = m.RootElement;
        Assert.Equal("0.1.0+test", root.GetProperty("version").GetString());
        Assert.Equal(2, root.GetProperty("userMarks").GetInt32()); // the truncated last line is not counted
        var files = root.GetProperty("files").EnumerateArray().ToDictionary(e => e.GetProperty("path").GetString()!, e => e.GetProperty("size").GetInt64());
        Assert.Equal(3, files["recordings/new.ibt"]);
        Assert.Equal(2, files["settings.json"]);
        Assert.DoesNotContain("manifest.json", files.Keys);
    }

    [Fact]
    public void Bundle_without_recordings_option()
    {
        Populate();
        var zip = DiagnosticBundle.Create(Data, Dest, new BundleOptions(IncludeRecordings: false), Info, DiagLog.Null, Clock, cancel: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Names(zip), n => n.StartsWith("recordings/", StringComparison.Ordinal));
        Assert.Contains("logs/fuelpace-20261109-1.jsonl", Names(zip));
    }

    [Fact]
    public void Bundle_works_with_missing_folders()
    {
        var zip = DiagnosticBundle.Create(Data, Dest, new BundleOptions(), Info, DiagLog.Null, Clock, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(["manifest.json", "system-info.txt"], Names(zip));
    }

    [Fact]
    public void Bundle_reads_files_another_process_is_writing()
    {
        Put("placeholder.txt", "x");
        var logPath = Path.Combine(Data, "logs", "fuelpace-20261109-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var recPath = Path.Combine(Data, "recordings", "live.ibt");
        Directory.CreateDirectory(Path.GetDirectoryName(recPath)!);
        using var log = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var rec = new FileStream(recPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        log.Write("{\"seq\":1}\n"u8); log.Flush();
        rec.Write("abc"u8); rec.Flush();

        var zip = DiagnosticBundle.Create(Data, Dest, new BundleOptions(), Info, DiagLog.Null, Clock, cancel: TestContext.Current.CancellationToken);
        Assert.Contains("logs/fuelpace-20261109-1.jsonl", Names(zip));
        Assert.Equal("abc", ReadEntry(zip, "recordings/live.ibt"));
    }

    [Fact]
    public void Two_bundles_in_the_same_second_do_not_overwrite()
    {
        var a = DiagnosticBundle.Create(Data, Dest, new BundleOptions(), Info, DiagLog.Null, Clock, cancel: TestContext.Current.CancellationToken);
        var b = DiagnosticBundle.Create(Data, Dest, new BundleOptions(), Info, DiagLog.Null, Clock, cancel: TestContext.Current.CancellationToken);
        Assert.NotEqual(a, b);
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public void Manifest_and_log_contain_no_absolute_paths()
    {
        Populate();
        AppDiagEvents.RegisterAll();
        var log = new DiagLog(DiagLevel.Info);
        var zip = DiagnosticBundle.Create(Data, Dest, new BundleOptions(), Info, log, Clock, cancel: TestContext.Current.CancellationToken);
        var manifest = ReadEntry(zip, "manifest.json");
        Assert.DoesNotContain(_root, manifest);
        Assert.DoesNotContain(Path.GetTempPath(), manifest);
        Assert.True(log.TryDequeueText(out var t));
        Assert.DoesNotContain(Dest, t.Message);
        Assert.Contains("FuelPace-diag-", t.Message);
    }

    [Fact]
    public void Emits_BundleCreated()
    {
        AppDiagEvents.RegisterAll();
        var log = new DiagLog(DiagLevel.Info);
        DiagnosticBundle.Create(Data, Dest, new BundleOptions(), Info, log, Clock, cancel: TestContext.Current.CancellationToken);
        var buf = new DiagRecord[8];
        int n = log.DrainRecords(buf);
        Assert.Contains(buf.Take(n), r => r.Event == AppDiagEvents.BundleCreated);
    }

    private void PutRandom(string rel, int mb)
    {
        var p = Path.Combine(Data, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var rnd = new Random(7);
        var buf = new byte[1 << 20];
        using var fs = File.Create(p);
        for (int i = 0; i < mb; i++)
        {
            rnd.NextBytes(buf); // incompressible: the zip has to work for every byte
            fs.Write(buf);
        }
    }

    [Fact] // A16: an exit during a bundle cancels it and nothing is left on the desktop
    public void A_cancelled_bundle_throws_and_leaves_no_file()
    {
        Populate();
        using var cts = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            DiagnosticBundle.Create(Data, Dest, new BundleOptions(), Info, DiagLog.Null, Clock, cts.Cancel, cts.Token));
        Assert.Empty(Directory.GetFiles(Dest));
    }

    [Fact] // A16: a big recording is not finished first; the copy stops within a chunk
    public async Task A_bundle_cancelled_in_the_middle_of_a_recording_stops_promptly()
    {
        Populate();
        PutRandom("recordings/20261109-140000_big.ibt", 96);
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => DiagnosticBundle.Create(Data, Dest, new BundleOptions(), Info, DiagLog.Null, Clock, null, cts.Token),
            TestContext.Current.CancellationToken);
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!Directory.Exists(Dest) || Directory.GetFiles(Dest, "*.tmp").Length == 0)
        {
            Assert.True(DateTime.UtcNow < end, "the bundle never started");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await Task.Delay(100, TestContext.Current.CancellationToken); // inside the recording by now
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"stopped {sw.Elapsed.TotalMilliseconds:F0} ms after the cancel");
        Assert.Empty(Directory.GetFiles(Dest));
    }
}
