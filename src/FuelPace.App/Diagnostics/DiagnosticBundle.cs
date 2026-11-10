using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Diagnostics;

public sealed record BundleOptions(bool IncludeRecordings = true, int MaxRecordings = 2);

/// <summary>
/// Builds <c>FuelPace-diag-yyyyMMdd-HHmmss.zip</c> (what the driver brings back): <c>logs/</c>, <c>settings.json</c> (and any
/// <c>.corrupt</c> copy), <c>profiles/</c> (including <c>.pending-*</c> and <c>.corrupt</c>), the newest recordings, <c>perf/</c>
/// when present, <c>system-info.txt</c> and <c>manifest.json</c>. Files are streamed into the zip (never loaded whole) and opened
/// with <c>FileShare.ReadWrite | Delete</c> because the Agent may still be writing them. Blocking I/O: call off the UI thread.
/// </summary>
public static class DiagnosticBundle
{
    private const int CopyBuffer = 81920;

    /// <param name="flush">Called before zipping (the sink's <c>FlushNow</c>) so the newest log lines are on disk.</param>
    public static string Create(string appDataRoot, string destDir, BundleOptions o,
        IReadOnlyDictionary<string, string> systemInfo, DiagLog log, TimeProvider clock, Action? flush = null)
    {
        if (flush is not null)
        {
            try { flush(); } catch { /* a failing flush must not prevent the bundle */ }
        }

        Directory.CreateDirectory(destDir);
        var now = clock.GetLocalNow();
        string baseName = "FuelPace-diag-" + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string zipPath = Path.Combine(destDir, baseName + ".zip");
        for (int n = 2; File.Exists(zipPath); n++)
            zipPath = Path.Combine(destDir, baseName + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".zip");

        string tmp = zipPath + ".tmp";
        var files = new List<(string Path, long Size, bool Partial)>();
        var skipped = new List<string>();
        int marks = 0;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                void Add(string source, string entry, bool countMarks = false)
                {
                    FileStream src;
                    try
                    {
                        src = new FileStream(source, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, CopyBuffer, FileOptions.SequentialScan);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // vanished (log pruned) or locked exclusively; only the type is recorded (messages carry user paths)
                        skipped.Add(Describe(entry, ex));
                        return;
                    }
                    using (src)
                    {
                        // a failure writing the zip is not caught here: it fails the whole bundle
                        using var dst = zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
                        try
                        {
                            long size = countMarks ? CopyCountingMarks(src, dst, ref marks) : Copy(src, dst);
                            files.Add((entry, size, false));
                        }
                        catch (SourceReadException ex)
                        {
                            skipped.Add(Describe(entry, ex.InnerException!) + " (partial)");
                            files.Add((entry, ex.Copied, true));
                        }
                    }
                }

                foreach (var f in Sorted(Path.Combine(appDataRoot, "logs"), "*.jsonl"))
                    Add(f, "logs/" + Path.GetFileName(f), countMarks: true);
                foreach (var f in Sorted(appDataRoot, "settings.json*"))
                    Add(f, Path.GetFileName(f));
                foreach (var f in Sorted(Path.Combine(appDataRoot, "profiles"), "*"))
                    Add(f, "profiles/" + Path.GetFileName(f));
                foreach (var f in Sorted(Path.Combine(appDataRoot, "perf"), "*"))
                    Add(f, "perf/" + Path.GetFileName(f));
                if (o.IncludeRecordings)
                    foreach (var f in Newest(Path.Combine(appDataRoot, "recordings"), "*.ibt", o.MaxRecordings).OrderBy(Path.GetFileName, StringComparer.Ordinal))
                        Add(f, "recordings/" + Path.GetFileName(f));

                var sys = new StringBuilder();
                foreach (var (k, v) in systemInfo)
                    sys.Append(k).Append(": ").Append(v.Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
                WriteText(zip, "system-info.txt", sys.ToString());
                files.Add(("system-info.txt", Encoding.UTF8.GetByteCount(sys.ToString()), false));

                WriteText(zip, "manifest.json", BuildManifest(systemInfo, now, marks, files, skipped));
            }
            File.Move(tmp, zipPath);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        log.Emit(DiagLevel.Info, AppDiagEvents.BundleCreated, double.NaN, i0: files.Count + 1);
        log.Text(DiagLevel.Info, "app", "diagnostic bundle created: " + Path.GetFileName(zipPath));
        return zipPath;
    }

    private static string BuildManifest(IReadOnlyDictionary<string, string> info, DateTimeOffset now, int marks,
        List<(string Path, long Size, bool Partial)> files, List<string> skipped)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("version", info.TryGetValue(SystemInfoKeys.Version, out var v) ? v : AppInfo.Version);
            w.WriteString("createdUtc", now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteNumber("userMarks", marks);
            w.WriteStartArray("files");
            foreach (var (p, s, partial) in files)
            {
                w.WriteStartObject();
                w.WriteString("path", p);
                w.WriteNumber("size", s);
                if (partial) w.WriteBoolean("partial", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("skipped");
            foreach (var s in skipped) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string Describe(string entry, Exception ex) =>
        entry + ": " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reading the source failed mid-copy (not the zip); carries how many bytes made it into the entry.</summary>
    private sealed class SourceReadException(Exception inner, long copied) : Exception(null, inner)
    {
        public long Copied { get; } = copied;
    }

    private static int ReadSource(Stream src, byte[] buf, long copied)
    {
        try { return src.Read(buf, 0, buf.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new SourceReadException(ex, copied); }
    }

    private static string[] Sorted(string dir, string pattern)
    {
        try
        {
            if (!Directory.Exists(dir)) return [];
            var f = Directory.GetFiles(dir, pattern);
            Array.Sort(f, StringComparer.Ordinal);
            return f;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> Newest(string dir, string pattern, int count)
    {
        if (count <= 0 || !Directory.Exists(dir)) return [];
        try
        {
            return new DirectoryInfo(dir).GetFiles(pattern)
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(count).Select(f => f.FullName).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static long Copy(Stream src, Stream dst)
    {
        var buf = new byte[CopyBuffer];
        long total = 0;
        int n;
        while ((n = ReadSource(src, buf, total)) > 0) { dst.Write(buf, 0, n); total += n; }
        return total;
    }

    private static readonly byte[] MarkNeedle = "\"ev\":\"UserMark\""u8.ToArray();

    /// <summary>Copies a JSONL file counting complete lines that carry a <c>UserMark</c> (a truncated last line is not counted).</summary>
    private static long CopyCountingMarks(Stream src, Stream dst, ref int marks)
    {
        var buf = new byte[CopyBuffer];
        var line = new List<byte>(256);
        long total = 0;
        int n;
        while ((n = ReadSource(src, buf, total)) > 0)
        {
            dst.Write(buf, 0, n);
            total += n;
            for (int i = 0; i < n; i++)
            {
                if (buf[i] == (byte)'\n')
                {
                    if (line.Count > 0 && System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line).IndexOf(MarkNeedle) >= 0) marks++;
                    line.Clear();
                }
                else if (line.Count < 4096) line.Add(buf[i]); // marks sit near the start of the line; cap memory
            }
        }
        return total;
    }
}
