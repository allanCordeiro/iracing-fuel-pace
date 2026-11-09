using FuelPace.Core.Diagnostics;
using FuelPace.Core.Telemetry;

namespace FuelPace.Core.Laps;

/// <summary>
/// Splits the player's telemetry into laps. Runs every frame on the telemetry thread and allocates nothing:
/// the finished lap is built in a field and handed to the sink by <c>in</c>.
/// <list type="bullet">
/// <item>Primary boundary: <c>prevPct &gt; 0.9 &amp;&amp; pct &lt; 0.1</c>; time and fuel are interpolated to the line.</item>
/// <item>Secondary boundary: <c>Lap</c> grows with no wrap within 1 s; the lap closes on the frame that detects it
/// (1 s after the increment) and both the closed lap and the one that starts there are marked <c>ResetSeen</c>.</item>
/// <item>Only laps that start and end at the line carry a fuel profile.</item>
/// <item>A closed lap waits (single pending slot) for <c>LapLastLapTime</c> to change, up to 5 s.</item>
/// </list>
/// </summary>
public sealed class LapDetector
{
    public const double LapTimeTimeoutS = 5.0;
    public const double LapIncrementWindowS = 1.0;
    public const double MaxAccumDtS = 0.1;
    public const float FullThrottleMin = 0.99f;
    public const double ResetGapS = 1.0, JumpSlackM = 30;

    private const int KindWrap = 0, KindLapIncrement = 1;
    private const int SourceOfficial = 0, SourceMeasured = 1, SourceNone = 2;

    private readonly ILapSink _sink;
    private readonly DiagLog _log;

    // previous frame
    private bool _started;
    private double _prevTime;
    private float _prevPct, _prevFuel, _prevLlt, _prevSpeed;
    private int _prevLap;

    // lap in progress
    private double _startTime;
    private double _fuelUsed, _accumTime, _fullThrottleTime;
    private int _midLap, _lastFrameLap;
    private bool _startAtLine;
    private LapStats _stats;
    private FuelProfile101 _profile;
    private int _nextK;
    private float _minFuel;
    private LapReasons _triggered;

    // secondary boundary
    private double _lastWrapTime = double.NegativeInfinity;
    private bool _incPending;
    private double _incTime;
    private int _incFromLap;

    // closed lap waiting for its official time
    private bool _hasPending;
    private CompletedLap _pending;
    private float _pendingLlt;
    private double _pendingWait;

    // in-progress lap emitted by Flush
    private CompletedLap _flushed;

    public LapDetector(ILapSink sink, DiagLog? log = null)
    {
        _sink = sink;
        _log = log ?? DiagLog.Null;
    }

    public double TrackLengthM { get; private set; } = double.NaN;

    public double CurrentLapFuelUsedL => _started ? _fuelUsed : 0;
    public double CurrentLapDistPct => _started ? _prevPct : 0;
    public bool CurrentLapHasPit => _started && _stats.PitSeen;
    public double CurrentLapStartTime => _started ? _startTime : double.NaN;

    public void SetTrackLength(double meters) => TrackLengthM = meters;

    public void Update(in TelemetryFrame f)
    {
        if (!_started)
        {
            _started = true;
            _prevFuel = f.FuelLevel;
            StartLap(f.SessionTime, Math.Max(0f, f.LapDistPct), startObserved: false, atLine: false, in f);
            if (f.LapDistPct < 0) Trigger(LapReasons.Reset, f.LapDistPct, in f);
            Observe(in f, 0);
            Remember(in f);
            return;
        }

        double rawDt = f.SessionTime - _prevTime;
        double dt = Math.Clamp(rawDt, 0, MaxAccumDtS);
        float pp = _prevPct, pct = f.LapDistPct;
        double dUsed = Math.Max(0, (double)_prevFuel - f.FuelLevel);
        double ftDt = f.Throttle >= FullThrottleMin ? dt : 0;

        if (_hasPending)
        {
            _pendingWait += Math.Max(0, rawDt);
            if (f.LapLastLapTime != _pendingLlt) ResolvePending(f.LapLastLapTime, official: true, f.SessionTime);
            else if (_pendingWait >= LapTimeTimeoutS) ResolvePending(f.LapLastLapTime, official: false, f.SessionTime);
        }

        if (rawDt > MaxAccumDtS + 1e-6 || rawDt < 0)
            _log.Emit(DiagLevel.Debug, DiagEvent.FrameGap, f.SessionTime, 0, 0, rawDt, f.SessionTime);
        bool discontinuity = rawDt > ResetGapS || rawDt < 0;
        if (discontinuity) Trigger(LapReasons.Reset, rawDt, in f);

        if (pct < 0)
        {
            // Car not in world (LapDistPct = -1): no boundary, no position/profile state, the lap is contaminated.
            // Fuel and Lap memory stay untouched so the next real frame accounts for what was burnt meanwhile.
            Trigger(LapReasons.Reset, pct, in f);
            _stats.PitSeen |= f.OnPitRoad || f.PlayerCarInPitStall;
            _stats.IncidentsAtEnd = f.PlayerCarMyIncidentCount;
            _prevTime = f.SessionTime;
            _prevLlt = f.LapLastLapTime;
            return;
        }

        if (pp >= 0 && !double.IsNaN(TrackLengthM))
        {
            double dPct = pct - pp;
            if (dPct > 0.5) dPct -= 1; else if (dPct < -0.5) dPct += 1;
            double meters = Math.Abs(dPct) * TrackLengthM;
            if (meters > Math.Max(f.Speed, _prevSpeed) * Math.Max(0, rawDt) * 2 + JumpSlackM)
            {
                discontinuity = true;
                Trigger(LapReasons.Reset, meters, in f);
            }
        }

        if (pp > 0.9f && pct < 0.1f)
        {
            double span = (1.0 - pp) + pct;
            // Never interpolate across a discontinuity: the whole frame belongs to the closing lap.
            // prevPct ≥ 1 (iRacing reports 1.0000x before wrapping): the line was at the previous frame.
            double a = discontinuity ? 1.0 : pp >= 1f ? 0.0 : Math.Clamp((1.0 - pp) / span, 0, 1);
            double endTime = discontinuity ? f.SessionTime : _prevTime + a * (f.SessionTime - _prevTime);

            double usedBefore = _fuelUsed;
            Accumulate(a * dUsed, a * dt, a * ftDt);
            if (f.PlayerTrackSurface == 0) _stats.OffTrackSeconds += a * dt;
            _stats.IncidentsAtEnd = f.PlayerCarMyIncidentCount;
            RecordProfile(pp, 1.0, usedBefore, _fuelUsed);
            CloseLap(endTime, KindWrap, pp, pct, a, in f);

            StartLap(endTime, 0, startObserved: true, atLine: true, in f);
            if (discontinuity) Trigger(LapReasons.Reset, rawDt, in f);
            Accumulate((1 - a) * dUsed, (1 - a) * dt, (1 - a) * ftDt);
            RecordProfile(0, pct, 0, _fuelUsed);
            Observe(in f, (1 - a) * dt);

            _lastWrapTime = f.SessionTime;
            _incPending = false;
        }
        else
        {
            if (f.Lap > _prevLap && !_incPending && f.SessionTime - _lastWrapTime > LapIncrementWindowS)
            {
                _incPending = true;
                _incTime = f.SessionTime;
                _incFromLap = _prevLap;
            }

            double usedBefore = _fuelUsed;
            Accumulate(dUsed, dt, ftDt);
            if (pct > pp) RecordProfile(pp, pct, usedBefore, _fuelUsed);
            Observe(in f, dt);

            if (_incPending && f.SessionTime - _incTime >= LapIncrementWindowS)
            {
                _incPending = false;
                _stats.ResetSeen = true;
                CloseLap(f.SessionTime, KindLapIncrement, pp, pct, double.NaN, in f);
                StartLap(f.SessionTime, pct, startObserved: true, atLine: false, in f);
                Trigger(LapReasons.Reset, 0, in f);
                Observe(in f, 0);
            }
        }

        Remember(in f);
    }

    /// <summary>Resolves the pending lap by timeout and emits the lap in progress with <c>EndObserved = false</c>.</summary>
    public void Flush()
    {
        if (!_started) return;
        if (_hasPending) ResolvePending(_prevLlt, official: false, _prevTime);

        _flushed = default;
        FillInProgress(ref _flushed);
        Emit(in _flushed);

        _started = false;
        _incPending = false;
        _lastWrapTime = double.NegativeInfinity;
    }

    /// <summary>
    /// Emits the closed lap still waiting for its official <c>LapLastLapTime</c> now, by the timeout rule (measured time),
    /// so a capture that stops inside the 5 s window still gets it. No-op when nothing is pending.
    /// </summary>
    public void ResolvePendingNow()
    {
        if (_started && _hasPending) ResolvePending(_prevLlt, official: false, _prevTime);
    }

    public bool TrySnapshotInProgress(out CompletedLap lap)
    {
        lap = default;
        if (!_started) return false;
        FillInProgress(ref lap);
        return true;
    }

    private void StartLap(double startTime, float pct, bool startObserved, bool atLine, in TelemetryFrame f)
    {
        _startTime = startTime;
        _startAtLine = atLine;
        _fuelUsed = _accumTime = _fullThrottleTime = 0;
        _midLap = -1;
        _stats = default;
        _stats.StartObserved = startObserved;
        _stats.IncidentsAtStart = _stats.IncidentsAtEnd = f.PlayerCarMyIncidentCount;
        _minFuel = Math.Min(_prevFuel, f.FuelLevel);
        _triggered = LapReasons.None;
        _profile = default;
        _nextK = Math.Clamp((int)(pct * 100) + 1, 1, 100);
    }

    private void Accumulate(double used, double dt, double fullThrottleDt)
    {
        _fuelUsed += used;
        _accumTime += dt;
        _fullThrottleTime += fullThrottleDt;
    }

    /// <summary>Per-frame observations attributed to the lap in progress.</summary>
    private void Observe(in TelemetryFrame f, double dt)
    {
        // An unconfirmed Lap increment does not name the lap yet.
        if (_midLap < 0 && f.LapDistPct >= 0.5f) _midLap = _incPending ? _incFromLap : f.Lap;
        _lastFrameLap = f.Lap;

        if (f.OnPitRoad || f.PlayerCarInPitStall) Trigger(LapReasons.Pit, 1, in f);
        if ((f.SessionFlags & LapValidator.CautionMask) != 0) Trigger(LapReasons.Caution, f.SessionFlags, in f);
        if (!f.IsOnTrack) Trigger(LapReasons.Reset, 0, in f);
        if (f.PlayerCarTowTime > 0) Trigger(LapReasons.Reset, f.PlayerCarTowTime, in f);

        _stats.IncidentsAtEnd = f.PlayerCarMyIncidentCount;
        if (_stats.IncidentsAtEnd > _stats.IncidentsAtStart)
            Trigger(LapReasons.Incident, _stats.IncidentsAtEnd - _stats.IncidentsAtStart, in f);

        if (f.PlayerTrackSurface == 0)
        {
            _stats.OffTrackSeconds += dt;
            if (_stats.OffTrackSeconds > LapValidator.OffTrackLimitS) Trigger(LapReasons.OffTrack, _stats.OffTrackSeconds, in f);
        }

        if (f.FuelLevel < _minFuel) _minFuel = f.FuelLevel;
        else if (f.FuelLevel > _minFuel + LapValidator.RefuelThresholdL) Trigger(LapReasons.Refuel, f.FuelLevel - _minFuel, in f);
    }

    /// <summary>Sets the stats flag behind <paramref name="reason"/> and logs the first trigger per reason and lap.</summary>
    private void Trigger(LapReasons reason, double value, in TelemetryFrame f)
    {
        switch (reason)
        {
            case LapReasons.Pit: _stats.PitSeen = true; break;
            case LapReasons.Caution: _stats.CautionSeen = true; break;
            case LapReasons.Reset: _stats.ResetSeen = true; break;
            case LapReasons.Refuel: _stats.RefuelSeen = true; break;
        }
        if ((_triggered & reason) != 0) return;
        _triggered |= reason;
        _log.Emit(DiagLevel.Debug, DiagEvent.LapReasonTriggered, f.SessionTime, f.Lap, (int)reason, value, f.LapDistPct);
    }

    private void Remember(in TelemetryFrame f)
    {
        _prevTime = f.SessionTime;
        _prevPct = f.LapDistPct;
        _prevFuel = f.FuelLevel;
        _prevLap = f.Lap;
        _prevLlt = f.LapLastLapTime;
        _prevSpeed = f.Speed;
    }

    /// <summary>Records cumulative fuel at each k/100 in (lo, hi], linear in pct between the two ends.</summary>
    private void RecordProfile(double lo, double hi, double usedLo, double usedHi)
    {
        while (_nextK < 100 && _nextK * 0.01 <= hi)
        {
            double frac = hi > lo ? Math.Clamp((_nextK * 0.01 - lo) / (hi - lo), 0, 1) : 1;
            _profile[_nextK] = (float)(usedLo + frac * (usedHi - usedLo));
            _nextK++;
        }
    }

    private int LapNumber(int fallback) => _midLap >= 0 ? _midLap : fallback;

    private double FullThrottlePct() => _accumTime > 0 ? 100.0 * _fullThrottleTime / _accumTime : double.NaN;

    private void CloseLap(double endTime, int kind, float prevPct, float pct, double interpFrac, in TelemetryFrame f)
    {
        if (_hasPending) ResolvePending(f.LapLastLapTime, official: false, f.SessionTime);

        ref var l = ref _pending;
        l.LapNumber = LapNumber(kind == KindLapIncrement ? _incFromLap : _lastFrameLap);
        l.StartTime = _startTime;
        l.EndTime = endTime;
        l.TimeS = double.NaN;
        l.MeasuredTimeS = Math.Max(0, endTime - _startTime);
        l.FuelUsedL = _fuelUsed;
        l.FullThrottlePct = FullThrottlePct();
        l.Stats = _stats;
        l.Stats.EndObserved = true;
        l.Reasons = LapValidator.Evaluate(in l.Stats);
        l.HasProfile = _startAtLine && kind == KindWrap && _fuelUsed > 0;
        if (l.HasProfile)
        {
            for (int k = _nextK; k < 100; k++) _profile[k] = (float)_fuelUsed;
            l.FuelProfile[0] = 0;
            for (int k = 1; k < 100; k++) l.FuelProfile[k] = (float)(_profile[k] / _fuelUsed);
            l.FuelProfile[100] = 1;
        }
        else
        {
            l.FuelProfile = default;
        }

        _hasPending = true;
        _pendingLlt = f.LapLastLapTime;
        _pendingWait = Math.Max(0, f.SessionTime - endTime);

        _log.Emit(DiagLevel.Debug, DiagEvent.LapBoundary, endTime, l.LapNumber, kind, prevPct, pct, interpFrac);
    }

    private void ResolvePending(float llt, bool official, double now)
    {
        ref var l = ref _pending;
        int source;
        if (official)
        {
            l.TimeS = llt > 0 ? llt : double.NaN;
            source = llt > 0 ? SourceOfficial : SourceNone;
        }
        else
        {
            l.TimeS = llt <= 0 ? double.NaN : l.MeasuredTimeS;
            source = llt <= 0 ? SourceNone : SourceMeasured;
        }
        _hasPending = false;
        _log.Emit(DiagLevel.Debug, DiagEvent.LapTimeResolved, now, l.LapNumber, source, l.TimeS, _pendingWait);
        Emit(in _pending);
    }

    private void FillInProgress(ref CompletedLap l)
    {
        l.LapNumber = LapNumber(_lastFrameLap);
        l.StartTime = _startTime;
        l.EndTime = _prevTime;
        l.TimeS = double.NaN;
        l.MeasuredTimeS = Math.Max(0, _prevTime - _startTime);
        l.FuelUsedL = _fuelUsed;
        l.FullThrottlePct = FullThrottlePct();
        l.Stats = _stats;
        l.Stats.EndObserved = false;
        l.Reasons = LapValidator.Evaluate(in l.Stats);
        l.HasProfile = false;
        l.FuelProfile = default;
    }

    private void Emit(in CompletedLap l)
    {
        _log.Emit(DiagLevel.Info, DiagEvent.LapCompleted, l.EndTime, l.LapNumber, (int)l.Reasons,
            l.TimeS, l.FuelUsedL, l.FullThrottlePct, l.MeasuredTimeS);
        _sink.OnLapCompleted(in l);
    }
}
