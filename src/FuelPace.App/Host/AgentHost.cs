using System.Globalization;
using System.Text;
using System.Text.Json;
using FuelPace.App.Diagnostics;
using FuelPace.App.Ipc;
using FuelPace.App.Overlay;
using FuelPace.App.Placement;
using FuelPace.App.Recording;
using FuelPace.App.Settings;
using FuelPace.Core.Capture;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Storage;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Host;

/// <summary>
/// The Windows Agent without Windows: settings, logs, telemetry, overlay, hotkeys, tray and IPC wired over the platform
/// interfaces of <c>Platform.cs</c>.
/// Threads: the telemetry thread (<c>FuelPace.Telemetry</c>) opens sources, steps the <see cref="TelemetryLoop"/> and runs the
/// <see cref="RenderScheduler"/> (allocation-free per tick, posts one cached delegate); the UI thread (<see cref="IUiDispatcher"/>)
/// owns the surface, hotkeys, tray, edit mode, visibility and every settings change; the I/O queue writes profiles, settings
/// and bundles (the pipeline saves captures on it too); IPC handlers run on pool threads and hand state changes to the UI
/// thread. Platform events may be raised on any thread: they are posted to the UI thread.
/// <see cref="Start"/> may be called from any thread; <see cref="Dispose"/> belongs on the UI thread (hotkeys are unregistered
/// there) after the message loop ended, once <see cref="Completion"/> finished; <see cref="EndSession"/> is the off-UI-thread
/// variant for a Windows session end.
/// settings.json is written only for explicit user changes, and never while the settings in use did not come from it: after
/// a load that fell back to the defaults (file locked or unreadable) the file is re-read every 5 s, before every snapshot and
/// before every change; until a read succeeds changes from the Settings window are refused and overlay drags are not saved.
/// </summary>
public sealed partial class AgentHost : IDisposable
{
    public const int HotkeyToggleEdit = 1, HotkeyToggleVisible = 2, HotkeyDebugMark = 3;

    internal const string FullscreenText =
        "O iRacing está em tela cheia exclusiva: o overlay não aparece por cima do jogo. Use o modo janela sem borda (borderless).";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FullscreenPeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RedrawPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>What the overlay shows without the sim: inactive, every value "—".</summary>
    private static readonly OverlayState NoSim = new()
    {
        State = SignalState.Inactive,
        FuelL = double.NaN, AutonomyLaps = double.NaN, AutonomyMin = double.NaN, LapProjectionL = double.NaN,
        LastLapL = double.NaN, MeanL = double.NaN, TargetL = double.NaN, LapsRemaining = double.NaN,
        FeasibleMinL = double.NaN, LastLapTimeS = double.NaN, MeanLapTimeS = double.NaN, BaselineLapTimeS = double.NaN,
        Insight = new Insight(InsightKind.None),
    };

    private readonly AgentOptions _o;
    private readonly ITelemetrySourceFactory _sources;
    private readonly IOverlaySurface _surface;
    private readonly IUiDispatcher _ui;
    private readonly IHotkeyService _hotkeys;
    private readonly ITrayIcon _tray;
    private readonly IMonitorProvider _monitorProvider;
    private readonly ISystemServices _sys;
    private readonly ITextMeasurer _measurer;
    private readonly TimeProvider _clock;
    private readonly SettingsStore _settingsStore;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _stop = new(false);
    /// <summary>Cancelled first thing in <see cref="Dispose"/>: an in-flight diagnostic bundle stops (ruling A16).</summary>
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Tap _tap;
    private readonly Lock _captureGate = new();

    // Created by Start.
    private DiagLog _log = DiagLog.Null;
    private DiagFileSink? _sink;
    private ProfileStore _profiles = null!;
    private BackgroundWorkQueue _io = null!;
    private TelemetryPipeline _pipeline = null!;
    private RenderScheduler _scheduler = null!;
    private IpcServer? _ipc;
    private Thread? _telemetry;
    private ITimer? _redrawTimer, _fullscreenTimer, _saveTimer, _autoHideTimer, _exitTimer;
    private Action _emitRedraws = null!, _checkFullscreen = null!, _retrySettings = null!, _connectionChanged = null!;
    private int _started, _disposed;
    private volatile bool _sessionEnding;

    // Shared between threads.
    private volatile bool _stopping, _debugMode, _simConnected, _editMode, _recorderWanted;
    private volatile bool _fullscreen;       // written by the UI thread only
    private volatile bool _settingsFallback; // written by the UI thread only (and Start)
    private volatile AppSettings _settings = new AppSettings().Normalize();
    private volatile SessionInfo? _sessionInfo;
    private volatile SessionTap? _lastSession;
    private volatile string[] _hotkeyFailures = [];
    private volatile IReadOnlyList<MonitorDesc> _monitors = [];
    private volatile TelemetryRecorder? _recorder;
    private double _lastSessionTime;
    private int _sessionNum, _savePending, _connections;
    private Task? _recorderDisposal;
    private List<CapturedLapDto> _captureLaps = [];
    private string? _lastCaptureId;
    private int _captureValid;

    // Telemetry thread only.
    private bool _sourceEnded;

    // UI thread only.
    private bool _userVisible = true, _shown, _hasShown, _hotkeysNotified, _rendered, _inactive, _hasPanel, _fullscreenNotified;
    private int _marks, _redraws, _seenConnections, _chromeW;
    private long _lastHash, _inactiveSince;
    private readonly DrawList _list = new(), _measure = new();
    private PixelRect _panel;
    private PixelRect? _bounds;
    private MonitorDesc? _monitor;
    private OverlayViewOptions? _view, _measureView;
    private AppSettings? _viewSettings;
    private double _viewDpi;

    private sealed record SessionTap(string Yaml, int Version, SessionInfo Info);

    public AgentHost(AgentOptions o, ITelemetrySourceFactory sources, IOverlaySurface surface, IUiDispatcher ui,
        IHotkeyService hotkeys, ITrayIcon tray, IMonitorProvider monitors, ISystemServices sys, ITextMeasurer measurer,
        TimeProvider clock)
    {
        _o = o;
        _sources = sources;
        _surface = surface;
        _ui = ui;
        _hotkeys = hotkeys;
        _tray = tray;
        _monitorProvider = monitors;
        _sys = sys;
        _measurer = measurer;
        _clock = clock;
        _settingsStore = new SettingsStore(o.AppDataRoot);
        _tap = new Tap(this);
    }

    /// <summary>Ends when the user exits (tray) or <see cref="AgentOptions.ExitAfter"/> elapsed; then call <see cref="Dispose"/>.</summary>
    public Task Completion => _completion.Task;

    /// <summary><c>--debug</c>, the <c>FuelPace.debug</c> marker or the Settings toggle.</summary>
    public bool DebugMode => _debugMode;

    internal DiagLog Log => _log;
    internal TelemetryPipeline Pipeline => _pipeline;
    internal AppSettings Settings => _settings;
    internal bool SimConnected => _simConnected;
    internal bool SettingsFallback => _settingsFallback;
    internal bool RecordingEnabled => _recorderWanted;
    internal bool RecorderRunning => _recorder is not null;
    internal bool TelemetryThreadAlive => _telemetry?.IsAlive ?? false;
    /// <summary>The state the last render decided to show (UI thread).</summary>
    internal OverlayState LastRendered { get; private set; }
    internal string LogsDir => Path.Combine(_o.AppDataRoot, "logs");
    internal bool WaitIo(TimeSpan timeout) => _io.WaitIdle(timeout);
    internal void FlushLogs() => _sink?.FlushNow();
    internal IFrameTap FrameTap => _tap;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The agent host is already started.");
        AppDiagEvents.RegisterAll();
        var settings = _settingsStore.Load();
        _settingsFallback = _settingsStore.LastLoadWasFallback;
        _settings = settings;
        _debugMode = _o.DebugFromCommandLine || _o.DebugMarker || settings.Debug.Enabled;

        _log = new DiagLog(_debugMode ? DiagLevel.Trace : DiagLevel.Info, clock: _clock);
        _sink = new DiagFileSink(_log, LogsDir);
        CrashHandler.Install(_log, _sink);
        _log.Emit(DiagLevel.Info, AppDiagEvents.Startup, 0);
        RefreshMonitors();
        _log.Text(DiagLevel.Info, "startup", DescribeStartup(settings));
        if (_settingsFallback)
            _log.Text(DiagLevel.Warn, "settings", "settings.json could not be read (locked or access denied): defaults in use; "
                + "it is re-read every 5 s and before every snapshot, and never written until a read succeeds");

        _profiles = new ProfileStore(_o.AppDataRoot, _log);
        _io = new BackgroundWorkQueue("FuelPace.IO", _log) { OnError = e => _log.Exception("io", e) };
        _pipeline = new TelemetryPipeline(settings.Strategy, _clock, _profiles, _io, _log);
        _pipeline.CaptureCompleted += OnCaptureCompleted;
        _pipeline.CaptureLapRecorded += OnCaptureLap;
        _scheduler = new RenderScheduler(_pipeline.Overlay, _ui, Render, _clock);
        _recorderWanted = _debugMode && settings.Debug.RecordTelemetry;

        _emitRedraws = EmitRedraws;
        _checkFullscreen = CheckFullscreen;
        _retrySettings = () => RunUi(() => TryRecoverSettings());
        _connectionChanged = ConnectionChanged;
        _hotkeys.Pressed += OnHotkeyPressed;
        _tray.Command += OnTrayCommand;
        _monitorProvider.Changed += OnMonitorsChanged;
        _surface.UserMoved += OnUserMoved;
        _surface.UserResized += OnUserResized;

        _ipc = new IpcServer(_o.PipeName, HandleIpcAsync, _log);
        _ipc.Start();

        _redrawTimer = _clock.CreateTimer(_ => _ui.Post(_emitRedraws), null, RedrawPeriod, RedrawPeriod);
        _fullscreenTimer = _clock.CreateTimer(_ =>
        {
            _ui.Post(_checkFullscreen);
            if (_settingsFallback) _ui.Post(_retrySettings);
        }, null, FullscreenPeriod, FullscreenPeriod);
        _saveTimer = _clock.CreateTimer(_ => SaveIfPending(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _autoHideTimer = _clock.CreateTimer(_ => _scheduler.InvalidateOptions(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_o.ExitAfter is { } after)
            _exitTimer = _clock.CreateTimer(_ => RequestExit("--exit-after"), null, after, Timeout.InfiniteTimeSpan);

        _ui.Post(UiStart);
        _telemetry = new Thread(TelemetryMain) { Name = "FuelPace.Telemetry", IsBackground = true, Priority = ThreadPriority.Normal };
        _telemetry.Start();
    }

    // ---------------------------------------------------------------- telemetry thread

    private void TelemetryMain()
    {
        try
        {
            while (!_stopping)
            {
                ITelemetrySource? src = null;
                try { src = _sources.TryOpen(); }
                catch (Exception e) { _log.Exception("telemetry", e); }
                if (src is not null) RunConnection(src);
                if (!_stopping) Idle(RetryInterval);
            }
        }
        catch (Exception e) { _log.Exception("telemetry", e); }
        finally { DisposeRecorder(); }
    }

    /// <summary>One connection: steps the loop until the source ends or the host stops. The pipeline (and its baseline)
    /// lives across connections; the loop's end of source flushes it, which also restarts lap detection.</summary>
    private void RunConnection(ITelemetrySource src)
    {
        _sourceEnded = false;
        _lastSession = null;
        try
        {
            ApplyRecorder();
            // Disposed at the end of this block, before the source: its session-info worker reads the source.
            using var loop = new TelemetryLoop(src, _pipeline, _profiles, inlineSessionInfo: _o.ReplayPath is not null, _log, _tap);
            while (!_stopping)
            {
                if (!loop.Step() || _sourceEnded) break;
                if (_recorderWanted != (_recorder is not null)) ApplyRecorder();
                _scheduler.Check();
            }
            if (!_sourceEnded)
            {
                // Stopping with the source still open: save an active capture and close the recording.
                _pipeline.Flush();
                _tap.OnSourceEnded();
            }
        }
        catch (Exception e) { _log.Exception("telemetry", e); }
        finally
        {
            try { src.Dispose(); }
            catch (Exception e) { _log.Exception("telemetry", e); }
            OnDisconnected();
        }
    }

    /// <summary>Waits <paramref name="d"/> of the host clock without data, still letting option changes render.</summary>
    private void Idle(TimeSpan d)
    {
        long start = _clock.GetTimestamp();
        while (!_stopping && _clock.GetElapsedTime(start) < d)
        {
            if (_recorderWanted != (_recorder is not null)) ApplyRecorder();
            _scheduler.Check();
            _stop.Wait(IdlePoll);
        }
    }

    private void OnConnected()
    {
        _simConnected = true;
        Interlocked.Increment(ref _connections);
        _ui.Post(_connectionChanged);
        _scheduler.InvalidateOptions();
    }

    private void OnDisconnected()
    {
        if (!_simConnected) return;
        _simConnected = false;
        _ui.Post(_connectionChanged);
        _scheduler.InvalidateOptions();
    }

    /// <summary>UI thread: a new sim connection may show the fullscreen notice again; without the sim nothing is fullscreen.</summary>
    private void ConnectionChanged() => RunUi(() =>
    {
        int c = Volatile.Read(ref _connections);
        if (c != _seenConnections)
        {
            _seenConnections = c;
            _fullscreenNotified = false;
        }
        if (!_simConnected && _fullscreen)
        {
            _fullscreen = false;
            Broadcast(new FullscreenChanged(false));
        }
    });

    /// <summary>Telemetry thread: starts or stops the recorder to match the debug settings.</summary>
    private void ApplyRecorder()
    {
        bool want = _recorderWanted;
        var cur = _recorder;
        if (want && cur is null)
        {
            try
            {
                var rec = new TelemetryRecorder(Path.Combine(_o.AppDataRoot, "recordings"), _log, _settings.Debug.MaxRecordingsGB);
                _recorder = rec;
                if (_lastSession is { } s) rec.OnSessionInfo(s.Yaml, s.Version, s.Info); // started mid-connection
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Text(DiagLevel.Error, "record", "recorder not started: " + e.Message);
                _recorderWanted = false;
            }
        }
        else if (!want && cur is not null)
        {
            // Dispose writes what is in the ring, closes the file and joins its thread (up to 10 s): not on this thread.
            _recorder = null;
            var previous = _recorderDisposal;
            _recorderDisposal = Task.Run(() =>
            {
                previous?.Wait();
                cur.Dispose();
            });
        }
    }

    private void DisposeRecorder()
    {
        var r = _recorder;
        _recorder = null;
        r?.Dispose();
    }

    /// <summary>Sees what the loop reads: marks the connection live, keeps the session info, feeds the recorder.</summary>
    private sealed class Tap(AgentHost h) : IFrameTap
    {
        public void OnFrame(in TelemetryFrame f)
        {
            if (!h._simConnected) h.OnConnected();
            Volatile.Write(ref h._lastSessionTime, f.SessionTime);
            if (f.SessionNum != Volatile.Read(ref h._sessionNum)) Volatile.Write(ref h._sessionNum, f.SessionNum);
            h._recorder?.OnFrame(in f);
        }

        public void OnSessionInfo(string yaml, int version, SessionInfo info)
        {
            h._sessionInfo = info;
            h._lastSession = new SessionTap(yaml, version, info);
            var rec = h._recorder;
            try { rec?.OnSessionInfo(yaml, version, info); }
            catch (ObjectDisposedException) { /* stopped meanwhile */ }
        }

        public void OnSourceEnded()
        {
            h._recorder?.OnSourceEnded();
            h._sourceEnded = true;
        }
    }

    private double SessionTime => Volatile.Read(ref _lastSessionTime);

    // ---------------------------------------------------------------- capture (telemetry thread)

    private void OnCaptureLap(CapturedLap lap, int valid, int total)
    {
        var dto = CapturedLapDto.From(lap);
        using (_captureGate.EnterScope())
        {
            if (total == 1) _captureLaps = [];
            _captureLaps.Add(dto);
            _captureValid = valid;
        }
        Broadcast(new CaptureLapAdded(dto, valid, total));
    }

    /// <summary>Ruling A2: the pipeline saves the capture itself; the host only mirrors the final laps and tells the clients.</summary>
    private void OnCaptureCompleted(CaptureRecord rec, string profileKey)
    {
        var laps = rec.Laps.ConvertAll(CapturedLapDto.From);
        using (_captureGate.EnterScope())
        {
            _captureLaps = laps;
            _captureValid = laps.Count(l => l.Valid);
        }
        Volatile.Write(ref _lastCaptureId, rec.Id);
        Broadcast(new CaptureFinished(profileKey, rec.Id));
    }

    // ---------------------------------------------------------------- UI thread

    /// <summary>Posts a platform event or request to the UI thread; nothing runs once stopping, failures are logged.</summary>
    private void OnUi(Action a) => _ui.Post(() => RunUi(a));

    private void RunUi(Action a)
    {
        if (_stopping) return;
        try { a(); }
        catch (Exception e) { _log.Exception("ui", e); }
    }

    private void UiStart() => RunUi(() =>
    {
        _surface.SetEditMode(false);
        UpdateTray();
        RegisterHotkeys();
        _scheduler.InvalidateOptions();
    });

    private void UpdateTray()
    {
        _tray.SetTooltip(_debugMode ? "FuelPace (debug) " + AppInfo.Version : "FuelPace " + AppInfo.Version);
        _tray.SetDebugMenu(_debugMode);
    }

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var h = _settings.Hotkeys;
        var failures = new List<string>();
        Register(HotkeyToggleEdit, h.ToggleEdit);
        Register(HotkeyToggleVisible, h.ToggleVisible);
        if (_debugMode) Register(HotkeyDebugMark, h.DebugMark);
        _hotkeyFailures = [.. failures];
        if (failures.Count > 0 && !_hotkeysNotified)
        {
            _hotkeysNotified = true; // Review Focus 2: once; the Settings window keeps showing them in red
            _tray.Notify("FuelPace", failures.Count == 1
                ? $"O atalho {failures[0]} já está em uso por outro programa."
                : $"Os atalhos {string.Join(", ", failures)} já estão em uso por outro programa.");
        }

        void Register(int id, string text)
        {
            bool ok = false;
            if (Hotkey.TryParse(text, out var key))
            {
                try { ok = _hotkeys.Register(id, key); }
                catch (Exception e) { _log.Exception("input", e); }
            }
            _log.Emit(DiagLevel.Info, AppDiagEvents.HotkeyRegistered, SessionTime, id, ok ? 1 : 0);
            if (ok) return;
            failures.Add(text);
            _log.Text(DiagLevel.Warn, "input", $"hotkey {text} not registered (in use by another program?)");
        }
    }

    private void OnHotkeyPressed(int id) => OnUi(() =>
    {
        _log.Emit(DiagLevel.Info, AppDiagEvents.HotkeyPressed, SessionTime, id);
        switch (id)
        {
            case HotkeyToggleEdit: SetEditMode(!_editMode); break;
            case HotkeyToggleVisible: ToggleVisible(); break;
            case HotkeyDebugMark when _debugMode: InsertMark(); break;
        }
    });

    private void OnTrayCommand(TrayCommand c) => OnUi(() =>
    {
        _log.Emit(DiagLevel.Info, AppDiagEvents.TrayCommand, SessionTime, (int)c);
        switch (c)
        {
            case TrayCommand.OpenSettings: _sys.LaunchSettings(); break;
            case TrayCommand.ToggleVisible: ToggleVisible(); break;
            case TrayCommand.ToggleEdit: SetEditMode(!_editMode); break;
            case TrayCommand.InsertMark when _debugMode: InsertMark(); break;
            case TrayCommand.CreateBundle:
                _io.Enqueue(() =>
                {
                    if (CreateBundleNow(includeRecordings: true, null) is { } path)
                        OnUi(() => _tray.Notify("FuelPace", "Pacote de diagnóstico criado: " + Path.GetFileName(path)));
                });
                break;
            case TrayCommand.OpenLogs: _sys.OpenFolder(LogsDir); break;
            case TrayCommand.Exit: RequestExit("tray"); break;
        }
    });

    private void RequestExit(string reason)
    {
        if (_completion.Task.IsCompleted) return;
        _log.Text(DiagLevel.Info, "app", "exit requested: " + reason);
        _completion.TrySetResult();
    }

    private void SetEditMode(bool on)
    {
        if (on == _editMode) return;
        _editMode = on;
        _surface.SetEditMode(on);
        _log.Emit(DiagLevel.Info, AppDiagEvents.EditMode, SessionTime, on ? 1 : 0);
        InvalidateView();
    }

    private void ToggleVisible()
    {
        _userVisible = !_userVisible;
        _scheduler.InvalidateOptions();
    }

    private void InsertMark()
    {
        int n = ++_marks;
        _log.Emit(DiagLevel.Info, DiagEvent.UserMark, SessionTime, n);
        _tray.Notify("FuelPace", "Marcador " + n.ToString(CultureInfo.InvariantCulture));
    }

    private void InvalidateView()
    {
        _view = null;
        _measureView = null;
        _scheduler.InvalidateOptions();
    }

    private void OnMonitorsChanged() => OnUi(() =>
    {
        RefreshMonitors();
        InvalidateView();
    });

    /// <summary>An empty list (a display change in progress) keeps the last one, and so the last placement.</summary>
    private void RefreshMonitors()
    {
        IReadOnlyList<MonitorDesc> list;
        try { list = _monitorProvider.GetMonitors(); }
        catch (Exception e)
        {
            _log.Exception("overlay", e);
            list = [];
        }
        _log.Emit(DiagLevel.Info, AppDiagEvents.MonitorsChanged, SessionTime, list.Count);
        if (list.Count == 0)
        {
            _log.Text(DiagLevel.Warn, "overlay", "no monitor reported; keeping the last placement");
            return;
        }
        _monitors = list;
    }

    private void OnUserMoved(PixelRect r) => OnUi(() => UserPlaced(r, resized: false));
    private void OnUserResized(PixelRect r) => OnUi(() => UserPlaced(r, resized: true));

    /// <summary><paramref name="window"/> is the whole window; the panel sits <see cref="OverlayLayout.PanelOffset"/> inside it.</summary>
    private void UserPlaced(PixelRect window, bool resized)
    {
        var monitors = _monitors;
        if (monitors.Count == 0) return;
        double dpi = _monitor?.DpiScale ?? 1.0;
        var off = OverlayLayout.PanelOffset(_editMode, dpi);
        var panel = new PixelRect(window.X + Px(off.W), window.Y + Px(off.H),
            Math.Max(1, window.Width - _chromeW), _hasPanel ? _panel.Height : window.Height);
        _bounds = null; // the user moved the window: the next render sends the bounds even if the placement resolves the same
        ApplyUserChange(s =>
        {
            var o = s.Overlay;
            if (resized)
            {
                double scale = OverlayPlacement.ScaleFromUserResize(panel, _measure.Size, dpi);
                o = o with { Scale = Math.Round(scale, 2, MidpointRounding.AwayFromZero) };
            }
            return s with { Overlay = OverlayPlacement.FromUserMove(o, panel, monitors) };
        }, debounce: true);
    }

    /// <summary>A change made on the overlay (drag, resize). After a fallback load the file is re-read first so the change
    /// lands on the user's settings; while it still cannot be read the change applies now but is not saved.</summary>
    private void ApplyUserChange(Func<AppSettings, AppSettings> change, bool debounce)
    {
        bool persist = TryRecoverSettings();
        ApplySettings(change(_settings), save: persist && !debounce);
        if (!persist)
        {
            _log.Text(DiagLevel.Warn, "settings", "settings.json still unreadable: the change is applied but not saved");
            return;
        }
        if (debounce)
        {
            Volatile.Write(ref _savePending, 1);
            _saveTimer?.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// UI thread. True when settings.json is the source of the settings in use: no fallback, or a re-read (one attempt, no
    /// retry delay) just succeeded, which applies the user's settings and broadcasts them. Only then may settings be saved.
    /// </summary>
    private bool TryRecoverSettings()
    {
        if (!_settingsFallback) return true;
        var loaded = _settingsStore.Load(retryLocked: false);
        if (_settingsStore.LastLoadWasFallback) return false;
        _log.Text(DiagLevel.Info, "settings", "settings.json is readable again: the user's settings are in use");
        ApplySettings(loaded, save: false);
        _settingsFallback = false;
        Broadcast(new SettingsApplied(_settings));
        return true;
    }

    /// <summary>Applies everything live: strategy, hotkeys, autostart, debug mode (log level, recorder, tray, mark hotkey), view.</summary>
    private void ApplySettings(AppSettings next, bool save)
    {
        next = next.Normalize();
        var prev = _settings;
        _settings = next;
        if (next.Strategy != prev.Strategy) _pipeline.SetConfig(next.Strategy);
        if (next.StartWithWindows != prev.StartWithWindows)
        {
            try { _sys.SetAutostart(next.StartWithWindows); }
            catch (Exception e) { _log.Exception("settings", e); }
        }
        bool debug = _o.DebugFromCommandLine || _o.DebugMarker || next.Debug.Enabled;
        bool debugChanged = debug != _debugMode;
        if (debugChanged)
        {
            _debugMode = debug;
            _log.Level = debug ? DiagLevel.Trace : DiagLevel.Info;
            _log.Text(DiagLevel.Info, "app", "debug mode " + (debug ? "on" : "off"));
            UpdateTray();
        }
        _recorderWanted = debug && next.Debug.RecordTelemetry;
        if (debugChanged || next.Hotkeys != prev.Hotkeys) RegisterHotkeys();
        InvalidateView();
        if (save) EnqueueSave(next);
    }

    private void SaveIfPending()
    {
        if (Interlocked.Exchange(ref _savePending, 0) == 1) EnqueueSave(_settings);
    }

    private void EnqueueSave(AppSettings s) => _io.Enqueue(() =>
    {
        if (_settingsFallback)
        {
            _log.Text(DiagLevel.Warn, "settings", "settings.json not saved: it has not been read successfully yet");
            return;
        }
        try
        {
            _settingsStore.Save(s);
            _log.Emit(DiagLevel.Info, AppDiagEvents.SettingsSaved, SessionTime);
            Broadcast(new SettingsApplied(s));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Text(DiagLevel.Error, "settings", "settings.json not saved: " + e.Message);
        }
    });

    private void EmitRedraws() => RunUi(() =>
    {
        _log.Emit(DiagLevel.Info, AppDiagEvents.OverlayRedraws, SessionTime, _redraws);
        _redraws = 0;
        EmitMemory();
    });

    /// <summary>PERF-05: working set, private bytes and the GC's share of them, once a minute.</summary>
    private void EmitMemory()
    {
        if (!_log.IsEnabled(DiagLevel.Info)) return;
        const double Mb = 1024.0 * 1024.0;
        long privateBytes;
        using (var self = System.Diagnostics.Process.GetCurrentProcess()) privateBytes = self.PrivateMemorySize64;
        var gc = GC.GetGCMemoryInfo();
        _log.Emit(DiagLevel.Info, AppDiagEvents.MemoryMinute, SessionTime, AppDomain.CurrentDomain.GetAssemblies().Length,
            GC.CollectionCount(2), Environment.WorkingSet / Mb, privateBytes / Mb, gc.HeapSizeBytes / Mb, gc.TotalCommittedBytes / Mb);
    }

    /// <summary>Every 5 s with the sim connected (Review Focus 1): a change is logged; going fullscreen notifies once per sim session.</summary>
    private void CheckFullscreen() => RunUi(() =>
    {
        if (!_simConnected) return;
        bool fs = _sys.IsFullscreenExclusiveActive();
        if (fs == _fullscreen) return;
        _fullscreen = fs;
        _log.Emit(fs ? DiagLevel.Warn : DiagLevel.Info, AppDiagEvents.FullscreenExclusive, SessionTime, fs ? 1 : 0);
        Broadcast(new FullscreenChanged(fs)); // an open Settings window's banner, at every change
        if (!fs || _fullscreenNotified) return;
        _fullscreenNotified = true;
        Broadcast(new Notice("warn", FullscreenText));
        _tray.Notify("FuelPace", FullscreenText);
    });

    /// <summary>
    /// UI thread, posted by the <see cref="RenderScheduler"/>. Placement contract (Task 7): <see cref="OverlayPlacement.Resolve"/>
    /// gets the panel size at Scale 1.0 (a second layout); the list drawn is built at Scale × DpiScale (physical px) and sizes
    /// the window. Only a list whose <see cref="DrawList.ContentHash"/> changed reaches the surface.
    /// </summary>
    private void Render()
    {
        if (_stopping) return;
        try { RenderCore(); }
        catch (Exception e) { _log.Exception("overlay", e); }
    }

    private void RenderCore()
    {
        var s = _settings;
        bool connected = _simConnected;
        var state = connected ? _pipeline.Overlay.Read() : NoSim;
        LastRendered = state;

        TimeSpan inactiveFor = TimeSpan.Zero;
        long now = _clock.GetTimestamp();
        if (state.State == SignalState.Inactive)
        {
            if (!_inactive)
            {
                _inactive = true;
                _inactiveSince = now;
                _autoHideTimer?.Change(AutoHidePolicy.InactiveDelay + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
            }
            inactiveFor = _clock.GetElapsedTime(_inactiveSince, now);
        }
        else _inactive = false;

        bool show = AutoHidePolicy.ShouldShow(_userVisible, s.Overlay.AutoHide, connected, in state, _editMode, inactiveFor);
        if (show != _shown || !_hasShown)
        {
            _surface.SetVisible(show);
            _shown = show;
            _hasShown = true;
            int reason = _editMode || !_userVisible ? 0 : !connected ? 1 : 2; // catalog: 0 user, 1 no sim, 2 off track (inactive)
            _log.Emit(DiagLevel.Info, AppDiagEvents.Visibility, SessionTime, show ? 1 : 0, reason);
        }
        if (!show) return;

        var car = _pipeline.Overlay.CarShort;
        var track = _pipeline.Overlay.TrackShort;
        if (!ReferenceEquals(_viewSettings, s)) { _view = null; _measureView = null; _viewSettings = s; }
        _measureView ??= ViewOptions(s, 1.0, edit: false, dpi: 1.0);
        OverlayLayout.Build(in state, car, track, _measureView, _measurer, _measure);

        var monitors = _monitors;
        if (monitors.Count > 0)
        {
            _panel = OverlayPlacement.Resolve(s.Overlay, monitors, _measure.Size, out var monitor);
            _hasPanel = true;
            _monitor = monitor;
        }
        double dpi = _monitor?.DpiScale ?? 1.0;
        if (_view is null || _viewDpi != dpi)
        {
            _view = ViewOptions(s, s.Overlay.Scale, _editMode, dpi);
            _viewDpi = dpi;
        }
        OverlayLayout.Build(in state, car, track, _view, _measurer, _list);

        if (_hasPanel)
        {
            var off = OverlayLayout.PanelOffset(_editMode, dpi);
            var bounds = new PixelRect(_panel.X - Px(off.W), _panel.Y - Px(off.H),
                (int)Math.Ceiling(_list.Size.W), (int)Math.Ceiling(_list.Size.H));
            _chromeW = bounds.Width - _panel.Width; // kept: a gesture clears _bounds, a second one before the next render needs this
            if (_bounds != bounds)
            {
                _surface.SetBounds(bounds);
                _bounds = bounds;
                _log.Emit(DiagLevel.Info, AppDiagEvents.WindowPlaced, SessionTime, 0, 0, bounds.X, bounds.Y, s.Overlay.Scale, dpi);
            }
        }

        long hash = _list.ContentHash;
        if (_rendered && hash == _lastHash) return;
        _surface.Render(_list);
        _rendered = true;
        _lastHash = hash;
        _redraws++;
    }

    /// <summary>The panel is drawn at <paramref name="scale"/> × <paramref name="dpi"/>, the edit chrome at <paramref name="dpi"/>.</summary>
    private OverlayViewOptions ViewOptions(AppSettings s, double scale, bool edit, double dpi) => new(
        scale, s.Overlay.BackgroundOpacity, s.Overlay.ContentOpacity, s.Overlay.ShowPaceLine, s.Overlay.ShowInsightLine,
        edit, _debugMode, s.Hotkeys.ToggleEdit, s.Overlay.OffsetX, s.Overlay.OffsetY, dpi);

    private static int Px(float v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    // ---------------------------------------------------------------- startup text, privacy

    private string DescribeStartup(AppSettings settings)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"FuelPace {AppInfo.Version} starting; debug {(_debugMode ? "on" : "off")} ");
        sb.Append(CultureInfo.InvariantCulture,
            $"(command line {_o.DebugFromCommandLine}, marker {_o.DebugMarker}, settings {settings.Debug.Enabled})\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"options: appData {Privacy.Scrub(_o.AppDataRoot)}, replay {(_o.ReplayPath is null ? "-" : Path.GetFileName(_o.ReplayPath))}, ");
        sb.Append(CultureInfo.InvariantCulture,
            $"speed {_o.ReplaySpeed}, exitAfter {_o.ExitAfter?.TotalSeconds.ToString(CultureInfo.InvariantCulture) ?? "-"} s, pipe {_o.PipeName}\n");
        sb.Append("system:");
        foreach (var (k, v) in SystemInfo()) sb.Append(' ').Append(k).Append('=').Append(v).Append(';');
        sb.Append("\nsettings.json ").Append(_settingsFallback ? "unreadable (defaults in use)" : "loaded").Append(":\n");
        sb.Append(JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
        return sb.ToString();
    }

    /// <summary>The platform's system info plus the host's keys, scrubbed of the user's name and profile path (bundles may be
    /// shared publicly).</summary>
    private Dictionary<string, string> SystemInfo()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var (k, v) in _sys.CollectSystemInfo()) d[k] = v;
        }
        catch (Exception e) { _log.Exception("app", e); }
        d[SystemInfoKeys.Version] = AppInfo.Version;
        d[SystemInfoKeys.DebugMode] = _debugMode ? "on" : "off";
        d[SystemInfoKeys.OverlayBackend] = _surface.BackendName;
        d[SystemInfoKeys.FullscreenExclusive] = _fullscreen ? "yes" : "no";
        foreach (var k in d.Keys.ToList()) d[k] = Privacy.Scrub(d[k]);
        return d;
    }

    // ---------------------------------------------------------------- shutdown

    /// <summary>
    /// <see cref="Dispose"/> for a Windows session end (ruling A20), from a pool thread while the UI thread is blocked in
    /// <c>WM_ENDSESSION</c>: the hotkeys are left registered (the process exit frees them; unregistering would wait for the
    /// blocked UI thread until it timed out) so the budget goes to the capture, the recording, the I/O queue and the log.
    /// </summary>
    public void EndSession()
    {
        _sessionEnding = true;
        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (Volatile.Read(ref _started) == 0) return;
        _stopping = true;
        _stop.Set();
        _shutdown.Cancel();
        _exitTimer?.Dispose();
        _redrawTimer?.Dispose();
        _fullscreenTimer?.Dispose();
        _autoHideTimer?.Dispose();
        _saveTimer?.Dispose();
        _hotkeys.Pressed -= OnHotkeyPressed;
        _tray.Command -= OnTrayCommand;
        _monitorProvider.Changed -= OnMonitorsChanged;
        _surface.UserMoved -= OnUserMoved;
        _surface.UserResized -= OnUserResized;

        // The telemetry thread saves an active capture (pipeline flush), closes the recording and disposes loop, then source.
        if (_telemetry is { } t && !t.Join(StopTimeout))
            _log.Text(DiagLevel.Error, "app", "telemetry thread did not stop in time");
        if (_recorderDisposal is { } rd && !rd.Wait(StopTimeout))
            _log.Text(DiagLevel.Error, "app", "a stopped recorder did not close in time");
        _ipc?.Dispose();
        if (!_sessionEnding)
        {
            try { _hotkeys.UnregisterAll(); }
            catch (Exception e) { _log.Exception("input", e); }
        }
        if (_io is { } io) // null when Start failed early
        {
            SaveIfPending(); // a drag that ended less than 500 ms ago
            if (!io.WaitIdle(StopTimeout)) _log.Text(DiagLevel.Error, "app", "I/O queue did not drain in time");
            io.Dispose();
        }

        _log.Emit(DiagLevel.Info, AppDiagEvents.Shutdown, SessionTime);
        _log.Text(DiagLevel.Info, "app", "shutdown");
        _completion.TrySetResult();
        _sink?.Dispose();
    }
}
