using FuelPace.Core.Baselines;
using FuelPace.Core.Capture;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Race;
using FuelPace.Core.Storage;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;

namespace FuelPace.Core.Pipeline;

/// <summary>
/// Per-tick wiring: lap detection, stint and leader tracking every frame (60 Hz), strategy every
/// <see cref="StrategyEvery"/>th frame (10 Hz), capture start/stop and profile persistence. <see cref="Tick"/> runs on the
/// telemetry thread and allocates nothing while no capture starts or stops. <see cref="OnLayout"/>,
/// <see cref="OnSessionContext"/>, <see cref="SetConfig"/> and the capture requests may come from any thread; they swap
/// references the next tick picks up.
/// </summary>
public sealed class TelemetryPipeline : ILapSink
{
    public const int StrategyEvery = 6;
    /// <summary>Strategy runs per <see cref="DiagEvent.StrategyTrace"/> (1 Hz).</summary>
    private const int TraceEvery = 10;
    private const int UnlimitedLaps = 32767;
    private const int NoRequest = 0, StartRequest = 1, StopRequest = 2;

    private readonly TimeProvider _clock;
    private readonly ProfileStore? _store;
    private readonly BackgroundWorkQueue? _io;
    private readonly DiagLog _log;
    private readonly LapDetector _detector;
    private readonly CaptureSession _capture;
    private readonly StintTracker _stint;
    private readonly LeaderPaceTracker _leader;
    private SignalHysteresis _hysteresis;

    // Written by any thread, read by the tick.
    private StrategyConfig _requestedCfg;
    private SessionContext? _ctx;
    private VarLayout? _layout;
    private int _request;
    private volatile bool _captureActive;

    // Telemetry thread only.
    private StrategyConfig _cfg;
    private SessionContext? _appliedCtx;
    private SessionContext? _captureCtx;
    private int _captureSessionNum;
    private long _ticks;
    private int _strategyRuns;
    private double _lastSessionTime;
    private double _lastLapL = double.NaN, _lastLapTimeS = double.NaN;
    private bool _lastLapInvalid;

    private SessionContext? _bopCtx;
    private float _bopPower = float.NaN, _bopWeight = float.NaN;
    private bool _bopMismatch;

    private bool _hasPrevState;
    private SignalState _prevRaw, _prevShown;
    private InsightKind _prevInsight;

    public TelemetryPipeline(StrategyConfig cfg, TimeProvider clock, ProfileStore? store, BackgroundWorkQueue? io, DiagLog? log = null)
    {
        _requestedCfg = _cfg = cfg;
        _clock = clock;
        _store = store;
        _io = io;
        _log = log ?? DiagLog.Null;
        _detector = new LapDetector(this, _log);
        _capture = new CaptureSession(clock, _log);
        _stint = new StintTracker(cfg.MeanWindow, _log);
        _leader = new LeaderPaceTracker(cfg.MeanWindow, _log);
    }

    /// <summary>Raised on the telemetry thread when a capture stops, with the profile key it belongs to. Subscribers get a
    /// copy (the I/O thread works on the original); an exception they throw is logged and does not stop the save.</summary>
    public event Action<CaptureRecord, string>? CaptureCompleted;

    /// <summary>Optional observer called on the telemetry thread for every lap the detector emits (including the lap in
    /// progress at <see cref="Flush"/>). Null by default; the call costs nothing when unset.</summary>
    public ILapSink? LapObserver { get; set; }

    public OverlayStatePublisher Overlay { get; } = new();

    public bool CaptureActive => _captureActive;

    /// <summary>Car name for the no-baseline insight (decision 3); empty before the first session context.</summary>
    public string CarShort => Volatile.Read(ref _ctx)?.CarShort ?? "";

    /// <summary>Track name for the no-baseline insight (decision 3); empty before the first session context.</summary>
    public string TrackShort => Volatile.Read(ref _ctx)?.TrackShort ?? "";

    internal TimeProvider Clock => _clock;

    public void OnLayout(VarLayout layout) => Volatile.Write(ref _layout, layout);

    public void OnSessionContext(SessionInfo info, ProfileDocument? profile)
    {
        var ctx = new SessionContext(info, profile);
        Volatile.Write(ref _ctx, ctx);
        Overlay.SetLabels(ctx.CarShort, ctx.TrackShort);
    }

    /// <summary>Takes effect on the next tick; a different <see cref="StrategyConfig.MeanWindow"/> only resizes the means (stint and history kept).</summary>
    public void SetConfig(StrategyConfig cfg) => Volatile.Write(ref _requestedCfg, cfg);

    /// <summary>Starts a capture on the next tick that has a session context. The last request wins.</summary>
    public void RequestCaptureStart() => Volatile.Write(ref _request, StartRequest);

    /// <summary>Stops the capture on the next tick. The last request wins.</summary>
    public void RequestCaptureStop() => Volatile.Write(ref _request, StopRequest);

    public void Tick(in TelemetryFrame f)
    {
        ApplyConfig();
        var ctx = Volatile.Read(ref _ctx);
        if (!ReferenceEquals(ctx, _appliedCtx))
        {
            _appliedCtx = ctx;
            if (ctx is not null) _detector.SetTrackLength(ctx.TrackLengthM);
        }
        _lastSessionTime = f.SessionTime;

        ApplyCaptureRequest(ctx, in f);
        // Review Focus 1: a capture never spans two profiles or two sessions; it is saved under the one it started in.
        if (_capture.IsActive && ctx is not null
            && ((!ReferenceEquals(ctx, _captureCtx) && ctx.ProfileKey != _captureCtx!.ProfileKey) || f.SessionNum != _captureSessionNum))
            StopCapture(auto: true, f.SessionTime);

        _detector.Update(in f);
        _stint.Update(in f);
        var layout = Volatile.Read(ref _layout);
        if (layout?.HasCarIdx ?? f.HasCarIdx) _leader.Update(in f);

        if (_ticks++ % StrategyEvery == 0) RunStrategy(ctx, layout, in f);
    }

    /// <summary>End of data: an active capture is stopped (and saved), then the detector emits the lap in progress.</summary>
    public void Flush()
    {
        if (_capture.IsActive)
        {
            bool manual = Interlocked.Exchange(ref _request, NoRequest) == StopRequest;
            StopCapture(auto: !manual, _lastSessionTime);
        }
        _detector.Flush();
    }

    public void OnLapCompleted(in CompletedLap lap)
    {
        _capture.OnLapCompleted(in lap);
        LapObserver?.OnLapCompleted(in lap);
        if (!lap.Stats.EndObserved) return; // the lap in progress emitted by Flush is not a "last lap"
        _stint.OnLapCompleted(in lap);
        _lastLapL = lap.FuelUsedL;
        _lastLapInvalid = lap.Reasons != LapReasons.None;
        _lastLapTimeS = lap.TimeS;
    }

    private void ApplyConfig()
    {
        var cfg = Volatile.Read(ref _requestedCfg);
        if (ReferenceEquals(cfg, _cfg)) return;
        if (cfg.MeanWindow != _cfg.MeanWindow)
        {
            // R15: a settings change must not restart the stint (stint-mode laps remaining) or drop lap history.
            _stint.Resize(cfg.MeanWindow);
            _leader.SetMeanWindow(cfg.MeanWindow);
        }
        _cfg = cfg;
    }

    private void RunStrategy(SessionContext? ctx, VarLayout? layout, in TelemetryFrame f)
    {
        var cfg = _cfg;
        var baseline = ctx?.Baseline;
        var session = ctx?.FindSession(f.SessionNum);
        var leader = _leader.Current;
        int me = layout is null || layout.Has(TelemetryVarId.PlayerCarIdx) ? f.PlayerCarIdx : ctx?.Info.DriverCarIdx ?? -1;
        bool recording = _capture.IsActive;
        ReadOnlySpan<float> profile = ctx is null ? default : ctx.BaselineProfile;

        var input = new StrategyInput
        {
            SessionTime = f.SessionTime,
            FuelL = f.FuelLevel,
            LapDistPct = f.LapDistPct,
            // R13: a lap whose start was not seen has partial fuel use; "—" beats a wrong number.
            LapProjectionL = _detector.CurrentLapHasPit || !_detector.CurrentLapStartObserved
                ? double.NaN
                : FuelProjection.Project(_detector.CurrentLapFuelUsedL, _detector.CurrentLapDistPct, profile),
            LastLapL = _lastLapL,
            LastLapInvalid = _lastLapInvalid,
            LastLapTimeS = _lastLapTimeS,
            StintMeanL = _stint.MeanFuelL,
            StintMeanLapS = _stint.MeanLapS,
            StintLapsDone = _stint.LapsDone,
            SessionTimeRemain = f.SessionTimeRemain,
            SessionLapsRemainEx = f.SessionLapsRemainEx,
            LeaderPaceS = leader.PaceS,
            LeaderLapDistPct = leader.LeaderLapDistPct,
            EstLapTimeS = ctx?.Info.EstLapTimeS ?? double.NaN,
            Inactive = ctx is null || f.OnPitRoad || f.PlayerCarInPitStall || !f.IsOnTrack || f.IsReplayPlaying
                || f.SessionState is not (4 or 5),
            IsElectric = layout?.IsElectric ?? false,
            Caution = (f.SessionFlags & LapValidator.CautionMask) != 0,
            IsRaceSession = session is not null && session.Type == "Race",
            LeaderFinished = f.SessionState == 5,
            IAmLeader = leader.LeaderCarIdx >= 0 && leader.LeaderCarIdx == me,
            BopMismatch = BopMismatch(ctx, in f),
            Recording = recording,
            RecValid = recording ? _capture.ValidCount : 0,
            RecTotal = recording ? _capture.TotalCount : 0,
            Race = RaceOf(session, f.SessionLapsRemainEx),
        };

        var s = FuelStrategyEngine.Compute(in input, baseline, cfg, ref _hysteresis);
        long versionBefore = Overlay.Version;
        Overlay.Publish(in s);
        EmitStrategyDiagnostics(in input, in s, baseline, cfg, versionBefore);
    }

    /// <summary>Decision 10: laps when the session has a lap count and the sim reports a finite remainder; else time; else unlimited.</summary>
    private static RaceKind RaceOf(SessionDef? s, int lapsRemainEx) =>
        s is null ? RaceKind.Unlimited
        : s.Laps.HasValue && lapsRemainEx < UnlimitedLaps ? RaceKind.Laps
        : s.TimeS.HasValue ? RaceKind.Time
        : RaceKind.Unlimited;

    /// <summary>True when the baseline has conditions and none matches this session's BoP; cached until the inputs change.</summary>
    private bool BopMismatch(SessionContext? ctx, in TelemetryFrame f)
    {
        if (ctx is null || ctx.Conditions.Length == 0) return false;
        if (!ReferenceEquals(ctx, _bopCtx) || !_bopPower.Equals(f.PlayerCarPowerAdjust) || !_bopWeight.Equals(f.PlayerCarWeightPenalty))
        {
            _bopCtx = ctx;
            _bopPower = f.PlayerCarPowerAdjust;
            _bopWeight = f.PlayerCarWeightPenalty;
            var current = new BopSignature(f.PlayerCarPowerAdjust, f.PlayerCarWeightPenalty, ctx.Info.TankCapacityL);
            bool matched = false;
            foreach (var c in ctx.Conditions)
                if (current.Matches(c)) { matched = true; break; }
            _bopMismatch = !matched;
        }
        return _bopMismatch;
    }

    private void EmitStrategyDiagnostics(in StrategyInput input, in OverlayState s, Baseline? baseline, StrategyConfig cfg, long versionBefore)
    {
        double refL = FirstKnown(s.LapProjectionL, s.LastLapL, s.MeanL);

        if (_log.IsEnabled(DiagLevel.Info))
        {
            // The engine only returns the displayed state; a fresh hysteresis gives the raw one.
            var fresh = default(SignalHysteresis);
            var raw = FuelStrategyEngine.Compute(in input, baseline, cfg, ref fresh).State;
            if (!_hasPrevState || raw != _prevRaw || s.State != _prevShown)
                _log.Emit(DiagLevel.Info, DiagEvent.StateChanged, input.SessionTime, (int)raw, (int)s.State,
                    s.TargetL, refL, s.LapsRemaining, s.FeasibleMinL);
            _prevRaw = raw;
        }

        if (!_hasPrevState || s.Insight.Kind != _prevInsight)
            _log.Emit(DiagLevel.Debug, DiagEvent.InsightChanged, input.SessionTime, (int)s.Insight.Kind, 0,
                s.Insight.A, s.Insight.B, s.Insight.C);

        if (Overlay.Version != versionBefore)
            _log.Emit(DiagLevel.Debug, DiagEvent.OverlayVersion, input.SessionTime, (int)s.State, (int)s.Insight.Kind,
                s.FuelL, s.TargetL, s.LapsRemaining, refL);

        if (_strategyRuns++ % TraceEvery == 0 && _log.IsEnabled(DiagLevel.Trace))
        {
            int flags = (input.Inactive ? 1 : 0) | (input.Caution ? 2 : 0) | (input.IAmLeader ? 4 : 0) | (input.BopMismatch ? 8 : 0);
            double myPace = Positive(input.StintMeanLapS, Positive(baseline?.LapMeanS ?? double.NaN, input.EstLapTimeS));
            _log.Emit(DiagLevel.Trace, DiagEvent.StrategyTrace, input.SessionTime, (int)input.Race, flags,
                input.LapProjectionL, input.LastLapL, input.StintMeanL, myPace);
        }

        _hasPrevState = true;
        _prevShown = s.State;
        _prevInsight = s.Insight.Kind;
    }

    private void ApplyCaptureRequest(SessionContext? ctx, in TelemetryFrame f)
    {
        if (Volatile.Read(ref _request) == NoRequest) return;
        int req = Interlocked.Exchange(ref _request, NoRequest);
        if (req == StartRequest && !_capture.IsActive)
        {
            if (ctx is null)
            {
                // No session info yet: keep the request for a later tick unless a newer one arrived.
                Interlocked.CompareExchange(ref _request, StartRequest, NoRequest);
                return;
            }
            StartCapture(ctx, in f);
        }
        else if (req == StopRequest && _capture.IsActive)
            StopCapture(auto: false, f.SessionTime);
    }

    private void StartCapture(SessionContext ctx, in TelemetryFrame f)
    {
        var conditions = new CaptureConditions(f.TrackTempCrew, f.AirTemp, f.TrackWetness,
            f.PlayerCarPowerAdjust, f.PlayerCarWeightPenalty, ctx.Info.TankCapacityL);
        _capture.Start(f.SessionTime, ctx.ProfileKey, ctx.FindSession(f.SessionNum)?.Type ?? "", conditions);
        _captureCtx = ctx;
        _captureSessionNum = f.SessionNum;
        _captureActive = true;
        _log.Emit(DiagLevel.Info, DiagEvent.CaptureStarted, f.SessionTime, f.Lap);
    }

    private void StopCapture(bool auto, double sessionTime)
    {
        // A lap that already ended but still waits for its official time must reach the capture complete.
        _detector.ResolvePendingNow();
        bool hasInProgress = _detector.TrySnapshotInProgress(out var inProgress);
        var rec = _capture.Stop(hasInProgress, in inProgress);
        var ctx = _captureCtx!;
        _captureCtx = null;
        _captureActive = false;

        int valid = 0;
        foreach (var l in rec.Laps)
            if (l.Valid) valid++;
        _log.Emit(DiagLevel.Info, DiagEvent.CaptureStopped, sessionTime, valid, rec.Laps.Count, auto ? 1 : 0);

        if (CaptureCompleted is { } handlers)
        {
            try { handlers(rec.Clone(), ctx.ProfileKey); }
            catch (Exception e) { _log.Exception("capture", e); }
        }

        if (_store is { } store && _io is { } io)
        {
            try { io.Enqueue(() => SaveCapture(store, ctx, rec)); }
            catch (ObjectDisposedException) { _log.Text(DiagLevel.Error, "capture", "capture not saved: I/O queue disposed"); }
        }
    }

    /// <summary>I/O thread: append the capture to the profile it started in, save, and use the result if that profile is still current.</summary>
    private void SaveCapture(ProfileStore store, SessionContext ctx, CaptureRecord rec)
    {
        var info = ctx.Info;
        var doc = store.Load(info.CarPath, info.TrackName) ?? ProfileDocument.CreateFor(info);
        ProfileOperations.AddCapture(doc, rec, _clock.GetUtcNow());
        store.Save(doc);

        // OnSessionContext with the new document, unless the session moved to another profile meanwhile.
        while (true)
        {
            var cur = Volatile.Read(ref _ctx);
            if (cur is null || cur.ProfileKey != ctx.ProfileKey) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _ctx, new SessionContext(cur.Info, doc), cur), cur)) return;
        }
    }

    private static double Positive(double x, double fallback) => x > 0 ? x : fallback;

    private static double FirstKnown(double a, double b, double c) => !double.IsNaN(a) ? a : !double.IsNaN(b) ? b : c;

    /// <summary>Immutable snapshot of what the tick needs from the session info and the profile.</summary>
    private sealed class SessionContext
    {
        public SessionContext(SessionInfo info, ProfileDocument? profile)
        {
            Info = info;
            Baseline = profile?.Baseline;
            BaselineProfile = Baseline?.FuelProfile ?? [];
            Conditions = Baseline?.Conditions is { } c ? [.. c] : [];
            Sessions = [.. info.Sessions];
            ProfileKey = info.ProfileKey;
            TrackLengthM = info.TrackLengthKm * 1000;
            CarShort = string.IsNullOrEmpty(info.CarScreenNameShort) ? info.CarScreenName : info.CarScreenNameShort;
            TrackShort = string.IsNullOrEmpty(info.TrackDisplayShortName)
                ? info.TrackDisplayName
                : string.IsNullOrEmpty(info.TrackConfigName) ? info.TrackDisplayShortName : info.TrackDisplayShortName + " " + info.TrackConfigName;
        }

        public SessionInfo Info { get; }
        public Baseline? Baseline { get; }
        public float[] BaselineProfile { get; }
        public BopSignature[] Conditions { get; }
        public SessionDef[] Sessions { get; }
        public string ProfileKey { get; }
        public double TrackLengthM { get; }
        public string CarShort { get; }
        public string TrackShort { get; }

        public SessionDef? FindSession(int num)
        {
            foreach (var s in Sessions)
                if (s.Num == num) return s;
            return null;
        }
    }
}
