using System.Globalization;

namespace FuelPace.Replay;

/// <summary>
/// <c>presentmon &lt;csv-with&gt; &lt;csv-without&gt;</c>: PERF-01 from two PresentMon captures (with and without the app). Reads the frame
/// time column (<c>FrameTime</c> of PresentMon 2.x, else <c>MsBetweenPresents</c> of 1.x; milliseconds, decimal point).
/// Method, per file, over its N frames: average fps = 1000 / mean frame time (frames over total time); 1% low = 1000 / mean of the
/// slowest ceil(1% of N) frame times (at least one); spikes = frames longer than 2x the mean frame time.
/// Verdict: average fps with &gt;= 99.5% of without, 1% low with &gt;= 99% of without, spikes with &lt;= spikes without.
/// Exit code: 0 approved, 1 rejected, 2 usage or unreadable input.
/// </summary>
internal static class PresentMonSummary
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Columns = ["FrameTime", "MsBetweenPresents"];

    internal readonly record struct Stats(int Frames, double AvgFps, double Low1Fps, int Spikes);

    public static int Run(string[] args, TextWriter w)
    {
        if (args.Length != 2)
        {
            w.WriteLine("presentmon needs <csv-with> <csv-without>");
            return 2;
        }
        if (!TryRead(args[0], w, out var with) || !TryRead(args[1], w, out var without)) return 2;

        w.WriteLine($"{"",-10}{"quadros",9}{"fps médio",12}{"1% low",10}{"quadros > 2×",14}");
        Row(w, "com", with);
        Row(w, "sem", without);

        double avgRatio = with.AvgFps / without.AvgFps, lowRatio = with.Low1Fps / without.Low1Fps;
        bool avgOk = avgRatio >= 0.995, lowOk = lowRatio >= 0.99, spikesOk = with.Spikes <= without.Spikes;
        w.WriteLine();
        w.WriteLine($"fps médio: {Pct(avgRatio)} do sem o app (mínimo 99,5%) {(avgOk ? "ok" : "FALHOU")}");
        w.WriteLine($"1% low: {Pct(lowRatio)} do sem o app (mínimo 99%) {(lowOk ? "ok" : "FALHOU")}");
        w.WriteLine($"quadros > 2× o médio: {with.Spikes} com, {without.Spikes} sem (não pode aumentar) {(spikesOk ? "ok" : "FALHOU")}");
        bool pass = avgOk && lowOk && spikesOk;
        w.WriteLine(pass ? "PERF-01: APROVADO" : "PERF-01: REPROVADO");
        return pass ? 0 : 1;
    }

    private static string Pct(double r) => (r * 100).ToString("F2", Inv) + "%";

    private static void Row(TextWriter w, string label, Stats s) =>
        w.WriteLine($"{label,-10}{s.Frames,9}{s.AvgFps.ToString("F1", Inv),12}{s.Low1Fps.ToString("F1", Inv),10}{s.Spikes,14}");

    private static bool TryRead(string path, TextWriter w, out Stats stats)
    {
        stats = default;
        if (!File.Exists(path)) { w.WriteLine($"not found: {path}"); return false; }
        var times = new List<double>();
        using (var r = new StreamReader(path))
        {
            var header = r.ReadLine()?.Split(',').Select(h => h.Trim().Trim('"')).ToArray();
            int col = -1;
            if (header is not null)
                foreach (var name in Columns)
                    if ((col = Array.IndexOf(header, name)) >= 0) break;
            if (col < 0) { w.WriteLine($"{path}: no FrameTime or MsBetweenPresents column"); return false; }
            string? line;
            while ((line = r.ReadLine()) is not null)
            {
                var f = line.Split(',');
                if (col < f.Length && double.TryParse(f[col].Trim('"'), NumberStyles.Float, Inv, out var ms) && ms > 0) times.Add(ms);
            }
        }
        if (times.Count == 0) { w.WriteLine($"{path}: no frames"); return false; }
        stats = Compute(times);
        return true;
    }

    internal static Stats Compute(List<double> times)
    {
        double mean = times.Average();
        var sorted = times.OrderByDescending(t => t).ToArray();
        int worst = Math.Max(1, (int)Math.Ceiling(times.Count * 0.01));
        double lowMean = sorted.Take(worst).Average();
        return new Stats(times.Count, 1000.0 / mean, 1000.0 / lowMean, times.Count(t => t > 2 * mean));
    }
}
