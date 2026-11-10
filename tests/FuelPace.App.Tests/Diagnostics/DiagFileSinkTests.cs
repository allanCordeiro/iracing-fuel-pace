using System.Reflection;
using System.Text.Json;
using FuelPace.App.Diagnostics;
using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Tests.Diagnostics;

public sealed class DiagFileSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fuelpace-sink-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string[] Files() => Directory.Exists(_dir)
        ? Directory.GetFiles(_dir, "fuelpace-*.jsonl").OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).ToArray()
        : [];

    private static string[] ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Writes_one_json_object_per_line()
    {
        var log = new DiagLog(DiagLevel.Info);
        using (var sink = new DiagFileSink(log, _dir))
        {
            log.Emit(DiagLevel.Info, DiagEvent.UserMark, 1.0, i0: 1);
            log.Text(DiagLevel.Info, "app", "hello \"world\"");
            log.Emit(DiagLevel.Info, DiagEvent.UserMark, 2.0, i0: 2);
            log.Emit(DiagLevel.Info, DiagEvent.UserMark, 3.0, i0: 3);
            sink.FlushNow();
        }
        var files = Files();
        Assert.Single(files);
        var lines = ReadLines(files[0]);
        Assert.Equal(4, lines.Length);
        foreach (var l in lines) JsonDocument.Parse(l).Dispose();
        // chronological by seq: the text was emitted second
        Assert.Equal(new long[] { 1, 2, 3, 4 }, lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("seq").GetInt64()).ToArray());
        Assert.Contains("msg", lines[1]);
    }

    [Fact]
    public void Rotates_at_max_file_size()
    {
        var log = new DiagLog(DiagLevel.Info);
        using (var sink = new DiagFileSink(log, _dir, maxFileBytes: 2048))
        {
            for (int i = 0; i < 200; i++) log.Emit(DiagLevel.Info, DiagEvent.UserMark, i, i0: i);
            sink.FlushNow();
        }
        var files = Files();
        Assert.True(files.Length >= 2);
        Assert.EndsWith("-1.jsonl", files[0]);
        Assert.EndsWith("-2.jsonl", files[1]);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 2048));
        Assert.Equal(200, files.Sum(f => ReadLines(f).Length));
    }

    [Fact]
    public void Deletes_oldest_beyond_total_cap()
    {
        var log = new DiagLog(DiagLevel.Info);
        using (var sink = new DiagFileSink(log, _dir, maxFileBytes: 1024, maxTotalBytes: 4096))
        {
            for (int i = 0; i < 500; i++) log.Emit(DiagLevel.Info, DiagEvent.UserMark, i, i0: i);
            sink.FlushNow();
        }
        var files = Files();
        Assert.DoesNotContain(files, f => f.EndsWith("-1.jsonl"));
        Assert.True(files.Sum(f => new FileInfo(f).Length) <= 4096);
    }

    [Fact]
    public void Dispose_flushes_pending_events()
    {
        var log = new DiagLog(DiagLevel.Info);
        var sink = new DiagFileSink(log, _dir);
        log.Emit(DiagLevel.Info, DiagEvent.UserMark, 1.0, i0: 7);
        log.Text(DiagLevel.Info, "app", "bye");
        sink.Dispose();
        Assert.Equal(2, Files().Sum(f => ReadLines(f).Length));
    }

    [Fact]
    public void App_events_have_catalog_entries()
    {
        AppDiagEvents.RegisterAll();
        var fields = typeof(AppDiagEvents).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(DiagEvent)).ToList();
        Assert.NotEmpty(fields);
        var values = new HashSet<DiagEvent>();
        foreach (var f in fields)
        {
            var e = (DiagEvent)f.GetValue(null)!;
            Assert.True((ushort)e >= 1000);
            Assert.True(values.Add(e), f.Name + " duplicates a value");
            var info = DiagEventCatalog.Get(e);
            Assert.NotNull(info);
            Assert.Equal(f.Name, info.Name);
        }
    }

    [Fact]
    public void Dropped_count_is_reported_in_file()
    {
        var log = new DiagLog(DiagLevel.Info, capacity: 2);
        using (var sink = new DiagFileSink(log, _dir))
        {
            for (int i = 0; i < 5; i++) log.Emit(DiagLevel.Info, DiagEvent.UserMark, i, i0: i);
            sink.FlushNow();
        }
        long dropped = 0;
        foreach (var l in Files().SelectMany(ReadLines))
        {
            var root = JsonDocument.Parse(l).RootElement;
            if (root.TryGetProperty("ev", out var ev) && ev.GetString() == "DiagDropped")
                dropped += root.GetProperty("count").GetInt64();
        }
        Assert.True(dropped > 0);
        Assert.Equal(log.Dropped, dropped);
    }

    [Fact]
    public void Unwritable_directory_keeps_draining_and_counts_lost_lines()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllText(_dir, "not a directory"); // logsDir is a file: every write fails
        try
        {
            var log = new DiagLog(DiagLevel.Info, capacity: 8);
            using var sink = new DiagFileSink(log, _dir);
            for (int round = 0; round < 5; round++)
            {
                for (int i = 0; i < 6; i++) log.Emit(DiagLevel.Info, DiagEvent.UserMark, i, i0: i);
                sink.FlushNow();
            }
            Assert.Equal(0, log.Dropped); // ring never overflowed
        }
        finally { File.Delete(_dir); }
    }

    [Fact]
    public void Crash_handler_logs_and_flushes()
    {
        var log = new DiagLog(DiagLevel.Info);
        using var sink = new DiagFileSink(log, _dir);
        CrashHandler.Handle(log, sink, new InvalidOperationException("boom"));
        var all = string.Join("\n", Files().SelectMany(ReadLines));
        Assert.Contains("boom", all);
        Assert.Contains("fatal", all);
    }

    [Fact] // an unobserved task exception does not end the process: an Error, not "fatal"
    public void Unobserved_task_exceptions_are_errors_not_fatal()
    {
        var log = new DiagLog(DiagLevel.Info);
        using var sink = new DiagFileSink(log, _dir);
        CrashHandler.HandleUnobserved(log, sink, new AggregateException(new InvalidOperationException("lost")));
        var line = Assert.Single(Files().SelectMany(ReadLines), l => l.Contains("lost"));
        Assert.Contains("\"lvl\":\"error\"", line);
        Assert.Contains("\"cat\":\"task\"", line);
        Assert.DoesNotContain("fatal", line);
    }

    [Fact]
    public void Unwritable_directory_beyond_pending_cap_drops_oldest_and_counts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dir)!);
        File.WriteAllText(_dir, "not a directory");
        try
        {
            var log = new DiagLog(DiagLevel.Info);
            using var sink = new DiagFileSink(log, _dir);
            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 10000; i++) log.Emit(DiagLevel.Info, DiagEvent.UserMark, i, i0: i);
                sink.FlushNow();
            }
            Assert.Equal(0, log.Dropped);
            Assert.Equal(10000, sink.LostLines); // 30000 lines - 20000 retained
        }
        finally { File.Delete(_dir); }
    }

    [Fact]
    public void Item_that_fails_to_format_is_counted_and_the_rest_of_the_batch_is_written()
    {
        var log = new DiagLog(DiagLevel.Info);
        long lostReported = 0;
        int normal = 0;
        using (var sink = new DiagFileSink(log, _dir, 20 << 20, 200 << 20, (w, t) =>
        {
            if (t.Message == "bad") throw new InvalidOperationException("format failure");
            DiagJsonFormatter.Write(w, in t);
        }))
        {
            log.Emit(DiagLevel.Info, DiagEvent.UserMark, 1.0, i0: 1);
            log.Text(DiagLevel.Info, "app", "bad");
            log.Emit(DiagLevel.Info, DiagEvent.UserMark, 2.0, i0: 2);
            sink.FlushNow();
            Assert.Equal(1, sink.LostLines);
        }
        foreach (var l in Files().SelectMany(ReadLines))
        {
            var root = JsonDocument.Parse(l).RootElement;
            if (root.GetProperty("ev").GetString() == "DiagDropped") lostReported += root.GetProperty("count").GetInt64();
            else normal++;
        }
        Assert.Equal(2, normal);
        Assert.Equal(1, lostReported);
    }

    [Fact]
    public void Concurrent_emitters_and_flushes_account_for_every_event()
    {
        const int threads = 4, perThread = 6000;
        var log = new DiagLog(DiagLevel.Info, capacity: 4096);
        var sink = new DiagFileSink(log, _dir);
        var emitters = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                if (i % 10 == 0) log.Text(DiagLevel.Info, "app", "t" + i);
                else log.Emit(DiagLevel.Info, DiagEvent.UserMark, i, i0: i);
            }
        })).ToList();
        var flusher = new Thread(() => { for (int i = 0; i < 50; i++) { sink.FlushNow(); Thread.Sleep(1); } });
        flusher.Start();
        emitters.ForEach(t => t.Start());
        emitters.ForEach(t => t.Join());
        flusher.Join();
        sink.Dispose();

        long lines = 0, markerCount = 0;
        foreach (var l in Files().SelectMany(ReadLines))
        {
            var root = JsonDocument.Parse(l).RootElement;
            if (root.TryGetProperty("ev", out var ev) && ev.GetString() == "DiagDropped") markerCount += root.GetProperty("count").GetInt64();
            else lines++;
        }
        Assert.Equal(threads * perThread, lines + log.Dropped + sink.LostLines);
        Assert.Equal(log.Dropped + sink.LostLines, markerCount);
    }
}
