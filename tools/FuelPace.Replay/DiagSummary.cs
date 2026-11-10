using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using FuelPace.Core.Laps;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Storage;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;

namespace FuelPace.Replay;

/// <summary>
/// <c>diag &lt;bundle.zip|folder&gt;</c>: triage summary of a diagnostic bundle (or of a <c>%APPDATA%\FuelPace</c> folder).
/// Malformed JSONL lines (a crash truncates the last one) are skipped and counted. Events from all rotated files are merged
/// ordered by timestamp (UTC) then <c>seq</c>: <c>seq</c> restarts at every Agent start, so it alone would interleave runs.
/// </summary>
internal static class DiagSummary
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int MarkWindowS = 30, MaxLinesPerSection = 300;

    private sealed record Ev(DateTime T, long Seq, string Lvl, string Cat, string? Name, string? Msg, JsonElement Json);

    private abstract class Source : IDisposable
    {
        public abstract IReadOnlyList<string> List(string prefix);
        public abstract Stream Open(string path);
        public virtual string? ReadText(string path)
        {
            try { using var r = new StreamReader(Open(path)); return r.ReadToEnd(); }
            catch (Exception ex) when (ex is IOException or FileNotFoundException) { return null; }
        }
        /// <summary>A real file for <paramref name="path"/> (extracted to a temporary file for zips); <paramref name="temp"/> is true when it must be deleted.</summary>
        public abstract string Materialize(string path, out bool temp);
        public virtual void Dispose() { }
    }

    private sealed class DirSource(string root) : Source
    {
        public override IReadOnlyList<string> List(string prefix)
        {
            var dir = Path.Combine(root, prefix.TrimEnd('/'));
            if (!Directory.Exists(dir)) return [];
            return Directory.GetFiles(dir).Select(f => prefix + Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }
        public override Stream Open(string path) => new FileStream(Path.Combine(root, path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        public override string Materialize(string path, out bool temp) { temp = false; return Path.Combine(root, path); }
    }

    private sealed class ZipSource(string zipPath) : Source
    {
        private readonly ZipArchive _zip = ZipFile.OpenRead(zipPath);
        public override IReadOnlyList<string> List(string prefix) =>
            _zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length > 0)
                .Select(e => e.FullName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        public override Stream Open(string path) =>
            (_zip.GetEntry(path) ?? throw new FileNotFoundException(path)).Open();
        public override string Materialize(string path, out bool temp)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fuelpace-diag-" + Guid.NewGuid().ToString("N") + ".ibt");
            using (var src = Open(path))
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            temp = true;
            return tmp;
        }
        public override void Dispose() => _zip.Dispose();
    }

    public static int Run(string[] args, TextWriter w)
    {
        if (args.Length != 1 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            w.WriteLine("diag needs <bundle.zip|folder>");
            return 2;
        }
        string path = args[0];
        Source src;
        try
        {
            if (Directory.Exists(path)) src = new DirSource(path);
            else if (File.Exists(path)) src = new ZipSource(path);
            else { w.WriteLine($"not found: {path}"); return 2; }
        }
        catch (InvalidDataException) { w.WriteLine($"not a valid zip: {path}"); return 2; }

        using (src)
        {
            Summarize(src, w);
            return 0;
        }
    }

    private static void Summarize(Source src, TextWriter w)
    {
        w.WriteLine("== FuelPace diag ==");
        var sys = src.ReadText("system-info.txt");
        if (sys is null) w.WriteLine("(sem system-info.txt)");
        else foreach (var l in sys.Split('\n', StringSplitOptions.RemoveEmptyEntries)) w.WriteLine("  " + l.TrimEnd('\r'));

        var logFiles = src.List("logs/").Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)).ToArray();
        int invalid = 0;
        var events = new List<Ev>();
        foreach (var f in logFiles) invalid += ReadLog(src, f, e => IsStructured(e) || e.Lvl is "error" or "warn" or "info", events);
        Sort(events);
        w.WriteLine();
        w.WriteLine($"Eventos: {events.Count} em {logFiles.Length} arquivo(s)" +
            (events.Count > 0 ? $", de {Stamp(events[0].T)} a {Stamp(events[^1].T)}" : ""));
        if (invalid > 0) w.WriteLine($"{invalid} linha(s) inválida(s) ignorada(s) (truncadas por um travamento?)");

        Section(w, "Erros e avisos", events.Where(e => e.Lvl is "error" or "warn"), e =>
        {
            w.WriteLine($"[{Stamp(e.T)}] {e.Lvl.ToUpperInvariant()} {Describe(e, full: true)}");
        });

        Section(w, "Linha do tempo", events.Where(e => e.Name is "StateChanged" or "CaptureStarted" or "CaptureStopped" or "LapCompleted"),
            e => w.WriteLine($"[{Stamp(e.T)}] {Describe(e, full: false)}"));

        var marks = events.Where(e => e.Name == "UserMark").ToList();
        if (marks.Count > 0)
        {
            // debug/trace lines are only worth keeping near a mark: second pass limited to those windows
            var wins = marks.Select(m => (From: m.T.AddSeconds(-MarkWindowS), To: m.T.AddSeconds(MarkWindowS))).ToArray();
            var around = new List<Ev>();
            foreach (var f in logFiles)
                ReadLog(src, f, e => e.Lvl is "debug" or "trace" && wins.Any(x => e.T >= x.From && e.T <= x.To), around);
            around.AddRange(events);
            Sort(around);
            foreach (var m in marks)
            {
                w.WriteLine();
                string number = m.Json.TryGetProperty("markNumber", out var mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetInt32().ToString(Inv) : "?";
                w.WriteLine($"-- Marcador {number} ({Stamp(m.T)}) ±{MarkWindowS} s --");
                int shown = 0, total = 0;
                foreach (var e in around.Where(e => e.T >= m.T.AddSeconds(-MarkWindowS) && e.T <= m.T.AddSeconds(MarkWindowS)))
                {
                    total++;
                    if (shown >= MaxLinesPerSection) continue;
                    shown++;
                    w.WriteLine($"{(ReferenceEquals(e, m) ? ">" : " ")} [{Stamp(e.T)}] {e.Lvl} {Describe(e, full: false)}");
                }
                if (total > shown) w.WriteLine($"  ... mais {total - shown} evento(s)");
            }
        }

        PerfTable(w, events);
        Recordings(src, w);
    }

    private static bool IsStructured(Ev e) => e.Name is not null;

    private static void Section(TextWriter w, string title, IEnumerable<Ev> items, Action<Ev> print)
    {
        w.WriteLine();
        w.WriteLine($"-- {title} --");
        var list = items.ToList();
        if (list.Count == 0) { w.WriteLine("(nenhum)"); return; }
        foreach (var e in list.Take(MaxLinesPerSection)) print(e);
        if (list.Count > MaxLinesPerSection) w.WriteLine($"... mais {list.Count - MaxLinesPerSection} omitido(s)");
    }

    private static void PerfTable(TextWriter w, List<Ev> events)
    {
        w.WriteLine();
        w.WriteLine("-- PerfMinute --");
        var perf = events.Where(e => e.Name == "PerfMinute").ToList();
        if (perf.Count == 0) { w.WriteLine("(nenhum)"); return; }
        var redraws = events.Where(e => e.Name == "OverlayRedraws").ToList();
        w.WriteLine($"{"hora (UTC)",-24}{"tick p99 µs",12}{"tick máx µs",12}{"gen0",6}{"WS MB",8}{"redesenhos/min",16}");
        foreach (var p in perf.Take(MaxLinesPerSection))
        {
            var r = redraws.FirstOrDefault(x => Math.Abs((x.T - p.T).TotalSeconds) <= 30);
            w.WriteLine($"{Stamp(p.T),-24}{Num(p.Json, "tickP99Us"),12}{Num(p.Json, "tickMaxUs"),12}{Num(p.Json, "gen0Delta"),6}{Num(p.Json, "workingSetMB"),8}{(r is null ? "-" : Num(r.Json, "perMinute")),16}");
        }
        if (perf.Count > MaxLinesPerSection) w.WriteLine($"... mais {perf.Count - MaxLinesPerSection} omitido(s)");
        double Max(string k) => perf.Max(p => p.Json.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0);
        w.WriteLine($"máximos: tick p99 {Max("tickP99Us").ToString("F0", Inv)} µs, tick máx {Max("tickMaxUs").ToString("F0", Inv)} µs, WS {Max("workingSetMB").ToString("F0", Inv)} MB");
    }

    private static string Num(JsonElement j, string key) =>
        j.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "-";

    private static void Recordings(Source src, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("-- Gravações --");
        var files = src.List("recordings/").Where(f => f.EndsWith(".ibt", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (files.Length == 0) { w.WriteLine("(nenhuma)"); return; }
        foreach (var f in files)
        {
            string path = src.Materialize(f, out bool temp);
            try
            {
                long size = new FileInfo(path).Length;
                var (duration, laps, records) = Analyze(path);
                string name = f["recordings/".Length..];
                w.WriteLine(records == 0
                    ? $"{name}: {size.ToString("N0", Inv)} bytes, sem registros legíveis"
                    : $"{name}: {size.ToString("N0", Inv)} bytes, {records.ToString(Inv)} registros, {duration.ToString("F1", Inv)} s, {laps.ToString(Inv)} voltas detectadas");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ArgumentException)
            {
                w.WriteLine($"{f}: ilegível ({ex.Message})");
            }
            finally
            {
                if (temp) try { File.Delete(path); } catch { }
            }
        }
    }

    private static (double DurationS, int Laps, int Records) Analyze(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "fuelpace-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var io = new BackgroundWorkQueue("diag io");
            var store = new ProfileStore(root);
            var pipeline = new TelemetryPipeline(new StrategyConfig(), TimeProvider.System, store, io);
            var counter = new LapCounter();
            pipeline.LapObserver = counter;
            using var source = new IbtTelemetrySource(path);
            var tap = new RangeTap();
            var loop = new TelemetryLoop(source, pipeline, store, inlineSessionInfo: true, tap: tap);
            while (loop.Step()) { }
            return (tap.Last - tap.First, counter.Laps, tap.Frames);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class LapCounter : ILapSink
    {
        public int Laps;
        public void OnLapCompleted(in CompletedLap lap) => Laps++;
    }

    private sealed class RangeTap : IFrameTap
    {
        public double First = double.NaN, Last;
        public int Frames;
        public void OnFrame(in TelemetryFrame f)
        {
            if (Frames++ == 0) First = f.SessionTime;
            Last = f.SessionTime;
        }
        public void OnSessionInfo(string yaml, int version, SessionInfo info) { }
        public void OnSourceEnded() { }
    }

    private static string Describe(Ev e, bool full)
    {
        if (e.Name is null)
        {
            var msg = e.Msg ?? "";
            if (!full) { int nl = msg.IndexOf('\n'); if (nl >= 0) msg = msg[..nl].TrimEnd('\r') + " ..."; }
            return $"{e.Cat}: {(full ? msg.Replace("\r", "").Replace("\n", "\n    ") : msg)}";
        }
        var sb = new System.Text.StringBuilder(e.Name);
        if (e.Json.TryGetProperty("st", out var st) && st.ValueKind == JsonValueKind.Number) sb.Append(" st=").Append(st.GetRawText());
        foreach (var p in e.Json.EnumerateObject())
        {
            if (p.Name is "t" or "seq" or "lvl" or "cat" or "ev" or "st") continue;
            sb.Append(' ').Append(p.Name).Append('=').Append(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText());
        }
        if (e.Name == "LapCompleted" && e.Json.TryGetProperty("lap", out var lap)) sb.Insert(0, "lap " + lap.GetRawText() + " ");
        return sb.ToString();
    }

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", Inv);

    private static void Sort(List<Ev> events) =>
        events.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.Seq.CompareTo(b.Seq));

    /// <summary>Parses one JSONL file; returns the number of malformed lines. Events passing <paramref name="keep"/> go to <paramref name="into"/>.</summary>
    private static int ReadLog(Source src, string file, Func<Ev, bool> keep, List<Ev> into)
    {
        int bad = 0;
        try
        {
            using var reader = new StreamReader(src.Open(file));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                if (!TryParse(line, out var e)) { bad++; continue; }
                if (keep(e)) into.Add(e);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { bad++; }
        return bad;
    }

    private static bool TryParse(string line, out Ev e)
    {
        e = null!;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return false;
            if (!r.TryGetProperty("t", out var tp) || tp.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(tp.GetString(), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return false;
            long seq = r.TryGetProperty("seq", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.TryGetInt64(out var s) ? s : 0;
            string lvl = r.TryGetProperty("lvl", out var lp) && lp.ValueKind == JsonValueKind.String ? lp.GetString()! : "info";
            string cat = r.TryGetProperty("cat", out var cp) && cp.ValueKind == JsonValueKind.String ? cp.GetString()! : "";
            string? name = r.TryGetProperty("ev", out var np) && np.ValueKind == JsonValueKind.String ? np.GetString() : null;
            string? msg = r.TryGetProperty("msg", out var mp) && mp.ValueKind == JsonValueKind.String ? mp.GetString() : null;
            e = new Ev(t, seq, lvl, cat, name, msg, r.Clone());
            return true;
        }
        catch (JsonException) { return false; }
    }
}
