using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FuelPace.App.Diagnostics;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Live;

/// <summary>The sim's data-valid event as <see cref="LiveConnection.WaitAndRead"/> uses it (the Agent wraps the named event).</summary>
public interface IDataValidSignal
{
    /// <summary>True when the event was signaled within <paramref name="timeoutMs"/>.</summary>
    bool Wait(int timeoutMs);

    /// <summary>Sleeps about <paramref name="ms"/> (the stuck-event guard).</summary>
    void Backoff(int ms);
}

/// <summary>
/// The portable decisions of the Agent's live iRacing source, over the shared-memory span the Agent maps (the Agent adds the
/// handles, the data-valid event and the dispose guard). One instance is one connection, as the host sees it: once
/// <see cref="TryRead"/> returned <see cref="ReadResult.Disconnected"/> it keeps returning it, and the host disposes the source
/// and opens a new one (which resets the whole pipeline: stint, leader, lap detection).
/// <list type="bullet">
/// <item>The status bit off for more than <see cref="NotConnectedGrace"/> is a disconnection; a shorter gap (the sim
/// reloading) keeps the connection, as long as the variable layout comes back the same.</item>
/// <item>A different layout (variables, offsets or types of the catalog) is a disconnection: the loop resolves the layout
/// once per connection, so a new layout needs a new connection (a full reset, by design).</item>
/// <item>No new frame for <see cref="StaleLimit"/> with the status bit still on (the sim died without clearing it) is a
/// disconnection: the irsdk client library uses the same 30 s timeout.</item>
/// <item>Ruling A21, <see cref="WaitAndRead"/>: the event's real semantics are not known. If it stays signaled, every wait
/// returns at once; after <see cref="StaleRunBeforeBackoff"/> wakeups in a row without a new tick each further one sleeps
/// <see cref="BackoffMs"/> (about one timer tick on Windows) so the telemetry thread cannot spin. Once a minute
/// <see cref="AppDiagEvents.LiveStats"/> logs the timed-out waits, the wakeups without a new tick, the frames, the back-offs,
/// the longest stale run and the frames read after a timed-out wait (an event that did not fire for new data: an auto-reset
/// event consumed by another tool).</item>
/// </list>
/// Threading: <see cref="TryConnect"/>, <see cref="TryRead"/>, <see cref="Layout"/> and <see cref="SessionInfoVersion"/> belong
/// to the telemetry thread; <see cref="ReadSessionInfoYaml"/> touches no mutable state and may run concurrently on the
/// session-info worker.
/// </summary>
public sealed class LiveConnection
{
    public static readonly TimeSpan NotConnectedGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StaleLimit = TimeSpan.FromSeconds(30);
    /// <summary>Reads of a session info the sim keeps rewriting before the version is skipped.</summary>
    public const int YamlAttempts = 3;
    private const int SessionInfoUpdateOffset = 12;
    /// <summary>The longest wait on the data-valid event: a missed signal costs at most this.</summary>
    public const int WaitMs = 200;
    public const int StaleRunBeforeBackoff = 3, BackoffMs = 2;
    public static readonly TimeSpan StatsPeriod = TimeSpan.FromMinutes(1);

    private readonly LiveSnapshotReader _snap;
    private readonly TimeProvider _clock;
    private readonly DiagLog _log;
    // WaitAndRead's counters for the current minute (telemetry thread).
    private long _statsStart;
    private int _timeouts, _staleWakeups, _frames, _backoffs, _staleRun, _maxStaleRun, _framesAfterTimeout;
    private double _lastSessionTime = double.NaN;
    private VarLayout? _layout;
    private long _notConnectedSince, _lastFrame;
    private bool _notConnected, _ended;

    public LiveConnection(TimeProvider? clock = null, DiagLog? log = null)
    {
        _clock = clock ?? TimeProvider.System;
        _log = log ?? DiagLog.Null;
        _snap = new LiveSnapshotReader(log: log);
    }

    /// <summary>The layout resolved by <see cref="TryConnect"/>.</summary>
    public VarLayout Layout => _layout ?? throw new InvalidOperationException("not connected");

    /// <summary>The header's <c>sessionInfoUpdate</c> as of the last read.</summary>
    public int SessionInfoVersion => _snap.SessionInfoUpdate;

    /// <summary>Tests: the snapshot reader (its torn-read hook).</summary>
    internal LiveSnapshotReader SnapshotForTests => _snap;

    /// <summary>Test hook, called between the session-info copy and the counter re-check.</summary>
    internal Action? BetweenYamlReadsForTests { get; set; }

    /// <summary>True when the sim is in a session and the layout is resolved; the newest frame is left for <see cref="TryRead"/>.</summary>
    public bool TryConnect(ReadOnlySpan<byte> memory)
    {
        var f = new TelemetryFrame();
        if (_snap.TryRead(memory, ref f) is not (LiveReadStatus.LayoutChanged or LiveReadStatus.Frame)) return false;
        _layout = _snap.Layout;
        _lastFrame = _clock.GetTimestamp();
        _statsStart = _lastFrame;
        return true;
    }

    /// <summary>
    /// Waits up to <see cref="WaitMs"/> on <paramref name="signal"/>, then <see cref="TryRead"/>; counts how the wait went and
    /// backs off when the event looks stuck signaled (see the class remarks). Allocation-free.
    /// </summary>
    public ReadResult WaitAndRead(ReadOnlySpan<byte> memory, ref TelemetryFrame frame, IDataValidSignal signal)
    {
        bool signaled = signal.Wait(WaitMs);
        var r = TryRead(memory, ref frame);
        if (r == ReadResult.Frame)
        {
            _frames++;
            _lastSessionTime = frame.SessionTime;
            if (!signaled) _framesAfterTimeout++;
        }
        if (!signaled)
        {
            _timeouts++;
            _staleRun = 0;
        }
        else if (r == ReadResult.NoData)
        {
            _staleWakeups++;
            if (++_staleRun > _maxStaleRun) _maxStaleRun = _staleRun;
            if (_staleRun > StaleRunBeforeBackoff)
            {
                _backoffs++;
                signal.Backoff(BackoffMs);
            }
        }
        else _staleRun = 0;
        MaybeEmitStats();
        return r;
    }

    private void MaybeEmitStats()
    {
        long now = _clock.GetTimestamp();
        if (_clock.GetElapsedTime(_statsStart, now) < StatsPeriod) return;
        _log.Emit(DiagLevel.Info, AppDiagEvents.LiveStats, _lastSessionTime, _timeouts, _staleWakeups, _frames, _backoffs,
            _maxStaleRun, _framesAfterTimeout);
        _statsStart = now;
        _timeouts = _staleWakeups = _frames = _backoffs = _maxStaleRun = _framesAfterTimeout = 0;
    }

    public ReadResult TryRead(ReadOnlySpan<byte> memory, ref TelemetryFrame frame)
    {
        if (_ended || _layout is null) return ReadResult.Disconnected;
        var status = _snap.TryRead(memory, ref frame);
        long now = _clock.GetTimestamp();
        switch (status)
        {
            case LiveReadStatus.Frame:
                _notConnected = false;
                _lastFrame = now;
                return ReadResult.Frame;
            case LiveReadStatus.NotConnected:
                if (!_notConnected)
                {
                    _notConnected = true;
                    _notConnectedSince = now;
                }
                else if (_clock.GetElapsedTime(_notConnectedSince, now) > NotConnectedGrace) return End();
                return ReadResult.NoData;
            case LiveReadStatus.LayoutChanged:
                if (!SameLayout(_layout, _snap.Layout!)) return End();
                _notConnected = false;
                _lastFrame = now; // back after a short gap: the stale timer restarts
                return ReadResult.NoData;
            default: // NoNewData, TornRead
                if (_clock.GetElapsedTime(_lastFrame, now) > StaleLimit) return End();
                return ReadResult.NoData;
        }
    }

    /// <summary>
    /// The session-info YAML, consistent with one <c>sessionInfoUpdate</c> value: the counter is read before and after the copy
    /// and the copy is retried when it changed. After <see cref="YamlAttempts"/> changing reads a <see cref="FormatException"/>
    /// skips this version (the loop logs it; the sim's next increment asks again). Empty when the memory is too short.
    /// </summary>
    public string ReadSessionInfoYaml(ReadOnlySpan<byte> memory)
    {
        if (memory.Length < IrsdkHeader.Size) return "";
        for (int i = 0; i < YamlAttempts; i++)
        {
            int before = Counter(memory);
            string yaml = _snap.ReadSessionInfoYaml(memory);
            BetweenYamlReadsForTests?.Invoke();
            if (Counter(memory) == before) return yaml;
        }
        throw new FormatException($"the session info changed while it was read ({YamlAttempts} attempts)");
    }

    private ReadResult End()
    {
        _ended = true;
        return ReadResult.Disconnected;
    }

    private static int Counter(ReadOnlySpan<byte> memory) =>
        Volatile.Read(ref Unsafe.As<byte, int>(ref MemoryMarshal.GetReference(memory.Slice(SessionInfoUpdateOffset, 4))));

    private static bool SameLayout(VarLayout a, VarLayout b)
    {
        if (a.HasCarIdx != b.HasCarIdx || !string.Equals(a.FuelUnit, b.FuelUnit, StringComparison.Ordinal)) return false;
        for (int i = 0; i < TelemetryVars.All.Count; i++)
            if (a[(TelemetryVarId)i] != b[(TelemetryVarId)i]) return false;
        return true;
    }
}
