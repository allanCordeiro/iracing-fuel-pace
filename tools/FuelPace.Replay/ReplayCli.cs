using System.Globalization;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Storage;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;

namespace FuelPace.Replay;

/// <summary>Replays an .ibt file through the Core pipeline and prints what the overlay would have shown.</summary>
public static class ReplayCli
{
    private const string Usage = "usage: FuelPace.Replay <file.ibt> [--speed max|1|10] [--capture] [--profiles <dir>]";

    public static int Run(string[] args, TextWriter output)
    {
        if (!TryParse(args, out var path, out var speed, out var capture, out var profilesDir, out var error))
        {
            output.WriteLine(error);
            output.WriteLine(Usage);
            return 2;
        }
        if (!File.Exists(path))
        {
            output.WriteLine($"file not found: {path}");
            return 2;
        }

        // --profiles is the root directory; the JSON files go in its "profiles" subfolder (same layout as %APPDATA%\FuelPace).
        var store = new ProfileStore(profilesDir ?? ProfileStore.DefaultRoot);

        using var io = new BackgroundWorkQueue("replay io");
        var pipeline = new TelemetryPipeline(new StrategyConfig(), TimeProvider.System, store, io);
        using var source = new IbtTelemetrySource(path, speed);
        var printer = new Printer(pipeline, output);
        pipeline.LapObserver = printer;
        SessionInfo? info = null;
        var tap = new InfoTap(i => { info = i; printer.Header(i); });
        var loop = new TelemetryLoop(source, pipeline, store, inlineSessionInfo: true, tap: tap);

        if (capture) pipeline.RequestCaptureStart();
        while (loop.Step()) printer.AfterStep();
        printer.AfterFlush();

        if (capture && info is not null)
        {
            io.WaitIdle(TimeSpan.FromSeconds(10));
            printer.Summary(store.Load(info.CarPath, info.TrackName));
        }
        return 0;
    }

    private static bool TryParse(string[] args, out string path, out ReplaySpeed speed, out bool capture, out string? profiles, out string error)
    {
        path = "";
        speed = ReplaySpeed.Max;
        capture = false;
        profiles = null;
        error = "";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--capture":
                    capture = true;
                    break;
                case "--speed":
                    if (++i >= args.Length) { error = "--speed needs a value"; return false; }
                    switch (args[i])
                    {
                        case "max": speed = ReplaySpeed.Max; break;
                        case "1": speed = ReplaySpeed.X1; break;
                        case "10": speed = ReplaySpeed.X10; break;
                        default: error = $"invalid --speed value: {args[i]}"; return false;
                    }
                    break;
                case "--profiles":
                    if (++i >= args.Length) { error = "--profiles needs a directory"; return false; }
                    profiles = args[i];
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) { error = $"unknown option: {args[i]}"; return false; }
                    if (path.Length > 0) { error = $"unexpected argument: {args[i]}"; return false; }
                    path = args[i];
                    break;
            }
        }
        if (path.Length == 0) { error = "missing .ibt file"; return false; }
        return true;
    }

    private sealed class InfoTap(Action<SessionInfo> onInfo) : IFrameTap
    {
        public void OnFrame(in TelemetryFrame f) { }
        public void OnSessionInfo(string yaml, int version, SessionInfo info) => onInfo(info);
        public void OnSourceEnded() { }
    }

    private sealed class Printer(TelemetryPipeline pipeline, TextWriter w) : ILapSink
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private int _overlayIn; // steps left until the strategy (10 Hz) has seen the lap just completed

        public void Header(SessionInfo i)
        {
            w.WriteLine($"car:   {i.CarScreenName} ({i.CarPath})");
            w.WriteLine($"track: {i.TrackDisplayName}{(i.TrackConfigName.Length > 0 ? " " + i.TrackConfigName : "")} ({i.TrackLengthKm.ToString("F3", Inv)} km)");
            foreach (var s in i.Sessions)
                w.WriteLine($"session {s.Num}: {s.Type}  laps={s.Laps?.ToString(Inv) ?? "-"}  time={(s.TimeS is { } t ? t.ToString("F0", Inv) + " s" : "-")}");
        }

        public void OnLapCompleted(in CompletedLap lap)
        {
            string time = double.IsNaN(lap.TimeS) ? "—" : lap.TimeS.ToString("F3", Inv);
            string mark = lap.Reasons == LapReasons.None ? "✓" : "✗ " + string.Join(',', LapReasonCodes.ToCodes(lap.Reasons));
            w.WriteLine($"lap {lap.LapNumber}  {time} s  {lap.FuelUsedL.ToString("F3", Inv)} L  {mark}");
            _overlayIn = TelemetryPipeline.StrategyEvery + 1;
        }

        public void AfterStep()
        {
            if (_overlayIn > 0 && --_overlayIn == 0) Overlay();
        }

        public void AfterFlush()
        {
            if (_overlayIn > 0) { _overlayIn = 0; Overlay(); }
        }

        private delegate int Fmt(double v, Span<char> dest);

        private static string N(Fmt f, double v)
        {
            Span<char> buf = stackalloc char[64];
            return new string(buf[..f(v, buf)]);
        }

        private void Overlay()
        {
            var s = pipeline.Overlay.Read();
            string Liters(double v) => N(OverlayText.Liters, v);
            string PerLap(double v) => N(OverlayText.PerLap, v);

            w.WriteLine($"  COMB  {Liters(s.FuelL)} L   {N(OverlayText.Laps, s.AutonomyLaps)} v · {N(OverlayText.Minutes, s.AutonomyMin)}min");
            w.WriteLine($"  VOLTA {PerLap(s.LapProjectionL)}   ÚLT {PerLap(s.LastLapL)}   MÉD {PerLap(s.MeanL)}  L/v");
            string seal = s.State switch
            {
                SignalState.Blue => "[NO ALVO]",
                SignalState.Red => "[ACIMA]",
                SignalState.NotFeasible => "[PIT]",
                SignalState.NoBaseline => "[SEM BASELINE]",
                SignalState.NoTarget => "[SEM LIMITE]",
                _ => "[INATIVO]",
            };
            string target = s.State == SignalState.NotFeasible
                ? $"NÃO FACTÍVEL  alvo {PerLap(s.TargetL)} · mín possível {PerLap(s.FeasibleMinL)}"
                : $"ALVO  {(s.TargetIsEstimate ? "~" : "")}{PerLap(s.TargetL)} L/v  faltam {N(OverlayText.Laps, s.LapsRemaining)} v";
            w.WriteLine($"  {target} {seal}");
            Span<char> ins = stackalloc char[160];
            int len = OverlayText.Insight(in s.Insight, pipeline.CarShort, pipeline.TrackShort, ins, out _);
            if (len > 0) w.WriteLine($"  {new string(ins[..len])}");
        }

        public void Summary(ProfileDocument? doc)
        {
            w.WriteLine();
            if (doc?.Baseline is not { } b)
            {
                w.WriteLine("Baseline: none (no valid laps captured)");
                return;
            }
            w.WriteLine($"Baseline: {b.ValidLaps} valid laps, confidence {b.Confidence}");
            w.WriteLine($"  fuel  mean {b.FuelMeanL.ToString("F3", Inv)} L  median {b.FuelMedianL.ToString("F3", Inv)}  std {b.FuelStdL.ToString("F3", Inv)}  min {b.FuelMinL.ToString("F3", Inv)}");
            w.WriteLine($"  lap   mean {b.LapMeanS.ToString("F3", Inv)} s  best {b.LapBestS.ToString("F3", Inv)}  std {b.LapStdS.ToString("F3", Inv)}");
            w.WriteLine($"  full throttle {b.FullThrottleMeanPct.ToString("F1", Inv)} %  autonomy {b.AutonomyLaps.ToString("F1", Inv)} laps / {b.AutonomyMin.ToString("F0", Inv)} min");
        }
    }
}
