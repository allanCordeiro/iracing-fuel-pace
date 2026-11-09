using System.Diagnostics;
using System.Globalization;
using System.Text;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Storage;
using FuelPace.Core.Telemetry;

namespace FuelPace.Core.Pipeline;

/// <summary>Sees every frame and session info the loop reads (the App's telemetry recorder). <see cref="OnFrame"/> runs on the
/// telemetry thread for every frame and must not allocate.</summary>
public interface IFrameTap
{
    void OnFrame(in TelemetryFrame f);
    void OnSessionInfo(string yaml, int version, SessionInfo info);
    void OnSourceEnded();
}

/// <summary>
/// Pulls frames from an <see cref="ITelemetrySource"/> into a <see cref="TelemetryPipeline"/>. When the session-info version
/// changes it parses the YAML and loads the profile, inline or on a background thread, and hands both to the pipeline.
/// Times each processed frame in a fixed histogram and emits <see cref="DiagEvent.PerfMinute"/> every 60 s of wall clock
/// (the pipeline's <see cref="TimeProvider"/>).
/// Dispose the loop BEFORE disposing the source: it stops the session-info worker, which calls
/// <see cref="ITelemetrySource.ReadSessionInfoYaml"/> (a native access violation on a pointer-backed live source once unmapped).
/// </summary>
public sealed class TelemetryLoop : IDisposable
{
    public const int BucketUs = 10, Buckets = 1000; // 10 µs buckets up to 10 ms, plus one overflow bucket
    private static readonly TimeSpan PerfPeriod = TimeSpan.FromSeconds(60);

    private readonly ITelemetrySource _source;
    private readonly TelemetryPipeline _pipeline;
    private readonly ProfileStore? _store;
    private readonly bool _inlineSessionInfo;
    private readonly DiagLog _log;
    private readonly IFrameTap? _tap;
    private readonly TimeProvider _clock;

    private TelemetryFrame _frame;
    private int _sessionVersion = int.MinValue;
    private int _latestVersion;
    private bool _finished, _disconnected; // _finished: End already ran for this connection (reset by the next frame)

    // Session-info worker: one background thread, newest requested version wins.
    private readonly object _workLock = new();
    private Thread? _worker;
    private bool _hasPending;
    private int _pendingVersion;
    private double _pendingTime;
    private bool _stop;

    private readonly int[] _histogram = new int[Buckets + 1];
    private int _samples;
    private double _maxUs;
    private DateTimeOffset _minuteStart;
    private int _gen0Start;
    private long _publishesStart, _droppedStart;

    public TelemetryLoop(ITelemetrySource source, TelemetryPipeline pipeline, ProfileStore? store, bool inlineSessionInfo,
        DiagLog? log = null, IFrameTap? tap = null)
    {
        _source = source;
        _pipeline = pipeline;
        _store = store;
        _inlineSessionInfo = inlineSessionInfo;
        _log = log ?? DiagLog.Null;
        _tap = tap;
        _clock = pipeline.Clock;

        var layout = ResolveLayout();
        // ITelemetrySource exposes neither the buffer length nor the tick rate; the .ibt source knows its tick rate.
        _log.Emit(DiagLevel.Info, DiagEvent.SourceOpened, 0, TelemetryVars.All.Count - layout.MissingCount, 0,
            source is IbtTelemetrySource ibt ? ibt.TickRate : double.NaN);
        StartMinute(_clock.GetUtcNow());
    }

    /// <summary>Reads one frame and processes it. False at end of stream (after flushing the pipeline); true otherwise.</summary>
    public bool Step()
    {
        switch (_source.TryRead(ref _frame))
        {
            case ReadResult.Frame:
                break;
            case ReadResult.EndOfStream:
                End(reason: 0);
                return false;
            case ReadResult.Disconnected:
                if (!_disconnected)
                {
                    _disconnected = true;
                    End(reason: 1);
                    lock (_workLock) _hasPending = false;           // no load for the old connection after this
                    Volatile.Write(ref _latestVersion, int.MinValue); // and an in-flight one is discarded
                    _sessionVersion = int.MinValue; // re-read the session info on reconnection
                }
                return true;
            default:
                return true;
        }

        long t0 = Stopwatch.GetTimestamp();
        _finished = false;
        if (_disconnected)
        {
            _disconnected = false;
            ResolveLayout();
        }
        int version = _source.SessionInfoVersion;
        if (version != _sessionVersion)
        {
            _sessionVersion = version;
            OnSessionInfoVersion(version, _frame.SessionTime);
        }
        _tap?.OnFrame(in _frame);
        _pipeline.Tick(in _frame);
        Record(Stopwatch.GetTimestamp() - t0);
        MaybeEmitPerfMinute();
        return true;
    }

    public void RunToEnd()
    {
        while (Step()) { }
    }

    private VarLayout ResolveLayout()
    {
        var layout = _source.Layout;
        _pipeline.OnLayout(layout);
        _log.Emit(DiagLevel.Info, DiagEvent.LayoutResolved, _frame.SessionTime, layout.MissingCount, layout.HasCarIdx ? 1 : 0,
            layout.IsElectric ? 1 : 0);
        return layout;
    }

    /// <summary>Flushes and notifies once per connection: a Disconnected followed by EndOfStream does not repeat it.</summary>
    private void End(int reason)
    {
        if (_finished) return;
        _finished = true;
        _pipeline.Flush();
        _tap?.OnSourceEnded();
        _log.Emit(DiagLevel.Info, DiagEvent.SourceEnded, _frame.SessionTime, reason);
    }

    private void OnSessionInfoVersion(int version, double sessionTime)
    {
        Volatile.Write(ref _latestVersion, version);
        if (_inlineSessionInfo)
        {
            LoadSessionInfo(version, sessionTime);
            return;
        }
        lock (_workLock)
        {
            _pendingVersion = version;
            _pendingTime = sessionTime;
            _hasPending = true;
            if (_stop) return;
            if (_worker is null)
            {
                var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "FuelPace session info" };
                worker.Start();
                _worker = worker;
            }
            else Monitor.Pulse(_workLock);
        }
    }

    /// <summary>Stops the session-info worker and waits for it (up to 2 s). Call before disposing the source.</summary>
    public void Dispose()
    {
        Thread? worker;
        lock (_workLock)
        {
            _stop = true;
            _hasPending = false;
            worker = _worker;
            Monitor.PulseAll(_workLock);
        }
        worker?.Join(TimeSpan.FromSeconds(2));
    }

    private void WorkerLoop()
    {
        while (true)
        {
            int version;
            double sessionTime;
            lock (_workLock)
            {
                while (!_hasPending && !_stop) Monitor.Wait(_workLock);
                if (_stop) return;
                _hasPending = false;
                version = _pendingVersion;
                sessionTime = _pendingTime;
            }
            try { LoadSessionInfo(version, sessionTime); }
            catch (Exception e) { _log.Exception("telemetry", e); }
        }
    }

    private void LoadSessionInfo(int version, double sessionTime)
    {
        string yaml;
        SessionInfo info;
        if (Volatile.Read(ref _stop)) return;
        try
        {
            yaml = _source.ReadSessionInfoYaml();
            info = SessionInfoParser.Parse(yaml);
        }
        catch (FormatException e)
        {
            _log.Text(DiagLevel.Warn, "telemetry", $"session info v{version} not parsed: {e.Message}");
            return;
        }

        ProfileDocument? profile = null;
        if (_store is not null)
        {
            try { profile = _store.Load(info.CarPath, info.TrackName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Text(DiagLevel.Error, "storage", $"profile not loaded for {info.ProfileKey}: {e.Message}");
            }
        }

        if (Volatile.Read(ref _latestVersion) != version) return; // superseded by a newer version
        _pipeline.OnSessionContext(info, profile);
        _log.Emit(DiagLevel.Info, DiagEvent.SessionInfoChanged, sessionTime, version);
        if (_log.IsEnabled(DiagLevel.Info)) _log.Text(DiagLevel.Info, "telemetry", Describe(version, info));
        _tap?.OnSessionInfo(yaml, version, info);
    }

    private static string Describe(int version, SessionInfo info)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"session info v{version}: car {info.CarScreenName} ({info.CarPath}), ");
        sb.Append(CultureInfo.InvariantCulture, $"track {info.TrackDisplayName} {info.TrackConfigName} ({info.TrackName}), sessions");
        foreach (var s in info.Sessions)
            sb.Append(CultureInfo.InvariantCulture, $" [{s.Num} {s.Type} laps={s.Laps?.ToString(CultureInfo.InvariantCulture) ?? "-"} time={s.TimeS?.ToString(CultureInfo.InvariantCulture) ?? "-"}]");
        return sb.ToString();
    }

    private void Record(long elapsedTicks)
    {
        double us = elapsedTicks * 1_000_000.0 / Stopwatch.Frequency;
        int bucket = us >= Buckets * BucketUs ? Buckets : (int)(us / BucketUs);
        _histogram[bucket]++;
        _samples++;
        if (us > _maxUs) _maxUs = us;
    }

    /// <summary>Upper edge of the bucket holding the 99th percentile, capped at the maximum seen.</summary>
    private double P99()
    {
        if (_samples == 0) return double.NaN;
        long target = (long)Math.Ceiling(_samples * 0.99);
        long cumulative = 0;
        for (int i = 0; i < Buckets; i++)
        {
            cumulative += _histogram[i];
            if (cumulative >= target) return Math.Min((i + 1) * BucketUs, _maxUs);
        }
        return _maxUs;
    }

    private void MaybeEmitPerfMinute()
    {
        var now = _clock.GetUtcNow();
        if (now - _minuteStart < PerfPeriod) return;
        if (_log.IsEnabled(DiagLevel.Info))
        {
            long publishes = _pipeline.Overlay.PublishCount;
            _log.Emit(DiagLevel.Info, DiagEvent.PerfMinute, _frame.SessionTime, GC.CollectionCount(0) - _gen0Start,
                (int)(publishes - _publishesStart), P99(), _maxUs, Environment.WorkingSet / (1024.0 * 1024.0),
                _log.Dropped - _droppedStart);
        }
        StartMinute(now);
    }

    private void StartMinute(DateTimeOffset now)
    {
        _minuteStart = now;
        _gen0Start = GC.CollectionCount(0);
        _publishesStart = _pipeline.Overlay.PublishCount;
        _droppedStart = _log.Dropped;
        Array.Clear(_histogram);
        _samples = 0;
        _maxUs = 0;
    }
}
