using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FuelPace.App.Host;
using FuelPace.App.Ipc;
using FuelPace.App.Overlay;
using FuelPace.App.Settings;
using FuelPace.Core.Display;
using FuelPace.Core.Strategy;

namespace FuelPace.App.SettingsUi.ViewModels;

/// <summary>
/// Mock 8: overlay, calculation, shortcuts and the live preview. The window never edits a settings file: changes go to the Agent
/// as <c>UpdateSettings</c> after a 300 ms debounce, built on the latest settings the Agent confirmed; whenever the Agent
/// announces settings (<c>SettingsApplied</c> or a new snapshot) that are not the echo of our own update, the whole model
/// is replaced and any edit still waiting is dropped.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    const int MaxRetries = 3;

    readonly AgentSender _out;
    readonly ISynchronizationContext _ui;
    readonly IDelayScheduler _sched;
    readonly ITextMeasurer _measurer;
    readonly OverlayState _previewState = GlancePreviews.Inputs["azul"];

    AppSettings _confirmed = new();
    readonly List<AppSettings> _inFlight = [];
    (AppSettings Old, AppSettings Edit)? _held;
    bool _hasSnapshot, _dirty;
    IReadOnlyList<string> _deviceNames = [], _failures = [];
    IDisposable? _timer;
    DrawList _preview = new();
    bool _loading = true, _debugMode;
    int _retries;

    internal OverlayViewModel(AgentSender sender, ISynchronizationContext ui, IDelayScheduler scheduler, ITextMeasurer? measurer = null)
    {
        _out = sender;
        _ui = ui;
        _sched = scheduler;
        _measurer = measurer ?? new FixedMetricsMeasurer();
        ToggleEdit = new HotkeyFieldViewModel("travar / editar posição", OnHotkeyEdited);
        ToggleVisible = new HotkeyFieldViewModel("mostrar / ocultar", OnHotkeyEdited);
        DebugMark = new HotkeyFieldViewModel("marcador de debug", OnHotkeyEdited);
        Load(_confirmed);
    }

    // ---- overlay
    public ObservableCollection<string> MonitorLabels { get; } = [];
    [ObservableProperty] public partial int SelectedMonitorIndex { get; set; } = -1;
    [ObservableProperty] public partial int OffsetX { get; set; }
    [ObservableProperty] public partial int OffsetY { get; set; }
    /// <summary>50–250.</summary>
    [ObservableProperty] public partial int ScalePercent { get; set; }
    [ObservableProperty] public partial int BackgroundOpacityPercent { get; set; }
    /// <summary>30–100.</summary>
    [ObservableProperty] public partial int ContentOpacityPercent { get; set; }
    [ObservableProperty] public partial bool ShowPaceLine { get; set; }
    /// <summary>"Ocultar fora da pista".</summary>
    [ObservableProperty] public partial bool AutoHide { get; set; }

    // ---- layout (PRD v3 §7)
    [ObservableProperty] public partial bool ShowDeltaBar { get; set; }
    /// <summary>The bar's half-range in L/lap, 0.05–0.50 in steps of 0.01.</summary>
    [ObservableProperty] public partial double DeltaBarScale { get; set; }
    /// <summary>True: the red block's complement is seconds per lap ("s/v"); false: percent.</summary>
    [ObservableProperty] public partial bool RedComplementSeconds { get; set; }
    [ObservableProperty] public partial bool ShowAvgInSupport { get; set; }
    public bool DeltaBarScaleEnabled => ShowDeltaBar;
    /// <summary>Always with a dot, like the other numbers of the page.</summary>
    public string DeltaBarScaleText => "±" + DeltaBarScale.ToString("0.00", CultureInfo.InvariantCulture) + " L/v";
    public string RedComplementHelp => "s/v precisa de uma baseline com ajuste consumo × tempo; sem ele, mostra %";

    // ---- calculation
    [ObservableProperty] public partial double ReserveL { get; set; }
    [ObservableProperty] public partial int MeanWindow { get; set; }
    [ObservableProperty] public partial double MaxSavingPercent { get; set; }
    [ObservableProperty] public partial double ExtraLaps { get; set; }
    /// <summary>True: automatic laps remaining; false: manual stint of <see cref="StintLaps"/> laps.</summary>
    [ObservableProperty] public partial bool LapsAuto { get; set; }
    [ObservableProperty] public partial int StintLaps { get; set; }

    // ---- shortcuts
    public HotkeyFieldViewModel ToggleEdit { get; }
    public HotkeyFieldViewModel ToggleVisible { get; }
    public HotkeyFieldViewModel DebugMark { get; }
    public bool ShowDebugMark => _debugMode;

    // Instance members so the view can bind to them.
    public string PositionInGameText => "Posicionar no jogo";
    public string PositionInGameHelp => "Destrava o overlay para arrastar sobre o iRacing. O jogo precisa estar em janela sem borda.";
    public string LapsAutoText => "Automático · por tempo usa o ritmo do líder geral";
    public string LapsStintText => "Manual · stint de voltas";

    /// <summary>The last problem worth telling the pilot (settings unreadable, reloaded, gave up).</summary>
    [ObservableProperty] public partial string? StatusMessage { get; private set; }

    public DrawList Preview => _preview;
    public string PreviewTitle => $"PRÉVIA · {ScalePercent}%";

    [RelayCommand]
    void PosicionarNoJogo() => _out.Send(new SetEditMode(true));

    // ------------------------------------------------------------ input from the Agent

    internal void Apply(AgentSnapshot s)
    {
        HoldEdit();
        _debugMode = s.DebugMode;
        OnPropertyChanged(nameof(ShowDebugMark));
        _deviceNames = s.MonitorDeviceNames ?? [];
        _failures = s.HotkeyFailures;
        MonitorLabels.Clear();
        foreach (var l in s.MonitorLabels) MonitorLabels.Add(l);
        StatusMessage = null;
        _hasSnapshot = true;
        ApplyRemote(s.Settings);
        ReapplyHeldEdit();
    }

    internal void OnSettingsApplied(SettingsApplied m)
    {
        HoldEdit();
        if (ApplyRemote(m.Settings)) StatusMessage = null;
        ReapplyHeldEdit();
    }

    /// <summary>The link is gone: nothing may be sent until a new snapshot says what the Agent has. What the pilot typed is kept
    /// and re-applied on top of that snapshot.</summary>
    internal void OnDisconnected()
    {
        HoldEdit();
        _hasSnapshot = false;
        _inFlight.Clear();
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Before the first snapshot (or after a disconnect) the model is not the Agent's: an edit typed meanwhile is
    /// remembered as a difference from that model and re-applied on the next real one.</summary>
    void HoldEdit()
    {
        if (_hasSnapshot || !_dirty) return;
        var edit = Build();
        if (edit != _confirmed) _held = (_held?.Old ?? _confirmed, edit);
    }

    void ReapplyHeldEdit()
    {
        if (_held is not var (old, edit) || !_hasSnapshot) return;
        _held = null;
        void Re<T>(T o, T e, Action<T> set) { if (!EqualityComparer<T>.Default.Equals(o, e)) set(e); }
        Re(old.Overlay.OffsetX, edit.Overlay.OffsetX, v => OffsetX = v);
        Re(old.Overlay.OffsetY, edit.Overlay.OffsetY, v => OffsetY = v);
        Re(old.Overlay.Scale, edit.Overlay.Scale, v => ScalePercent = (int)Math.Round(v * 100));
        Re(old.Overlay.BackgroundOpacity, edit.Overlay.BackgroundOpacity, v => BackgroundOpacityPercent = (int)Math.Round(v * 100));
        Re(old.Overlay.ContentOpacity, edit.Overlay.ContentOpacity, v => ContentOpacityPercent = (int)Math.Round(v * 100));
        Re(old.Overlay.ShowPaceLine, edit.Overlay.ShowPaceLine, v => ShowPaceLine = v);
        Re(old.Overlay.ShowDeltaBar, edit.Overlay.ShowDeltaBar, v => ShowDeltaBar = v);
        Re(old.Overlay.DeltaBarScaleLpl, edit.Overlay.DeltaBarScaleLpl, v => DeltaBarScale = v);
        Re(old.Overlay.RedComplement, edit.Overlay.RedComplement, v => RedComplementSeconds = v == RedComplement.Seconds);
        Re(old.Overlay.ShowAvgInSupport, edit.Overlay.ShowAvgInSupport, v => ShowAvgInSupport = v);
        Re(old.Overlay.AutoHide, edit.Overlay.AutoHide, v => AutoHide = v);
        Re(old.Overlay.MonitorDeviceName, edit.Overlay.MonitorDeviceName, v =>
        {
            int i = -1;
            for (int k = 0; k < _deviceNames.Count; k++)
                if (string.Equals(_deviceNames[k], v, StringComparison.OrdinalIgnoreCase)) i = k;
            if (i >= 0) SelectedMonitorIndex = i;
        });
        Re(old.Strategy.ReserveL, edit.Strategy.ReserveL, v => ReserveL = v);
        Re(old.Strategy.MeanWindow, edit.Strategy.MeanWindow, v => MeanWindow = v);
        Re(old.Strategy.MaxSavingFraction, edit.Strategy.MaxSavingFraction, v => MaxSavingPercent = Math.Round(v * 100, 2));
        Re(old.Strategy.ExtraLaps, edit.Strategy.ExtraLaps, v => ExtraLaps = v);
        Re(old.Strategy.Mode, edit.Strategy.Mode, v => LapsAuto = v == LapsMode.Auto);
        Re(old.Strategy.StintLaps, edit.Strategy.StintLaps, v => StintLaps = v);
        Re(old.Hotkeys.ToggleEdit, edit.Hotkeys.ToggleEdit, v => ToggleEdit.Text = v);
        Re(old.Hotkeys.ToggleVisible, edit.Hotkeys.ToggleVisible, v => ToggleVisible.Text = v);
        Re(old.Hotkeys.DebugMark, edit.Hotkeys.DebugMark, v => DebugMark.Text = v);
    }

    internal bool OnNotice(Notice n)
    {
        if (n.Text == AgentHost.SettingsUnreadableText)
        {
            _inFlight.Clear(); // the Agent did not take it: the next flush sends again
            if (_retries < MaxRetries)
            {
                _retries++;
                StatusMessage = "As configurações ainda não puderam ser lidas. Tentando de novo…";
                Restart(RetryDelay);
            }
            else StatusMessage = "Não foi possível salvar as configurações. Tente de novo.";
            return true;
        }
        if (n.Text == AgentHost.SettingsReloadedText)
        {
            StatusMessage = "Configurações recarregadas do arquivo; confira e tente novamente.";
            return true;
        }
        return false;
    }

    /// <returns>True when the settings were our own update coming back (the edits in the fields are kept).</returns>
    bool ApplyRemote(AppSettings s)
    {
        _confirmed = s;
        int i = _inFlight.IndexOf(s);
        if (i >= 0)
        {
            // Our own update coming back (updates answer in order): older ones are settled, newer ones still in flight.
            _inFlight.RemoveRange(0, i + 1);
            _retries = 0;
            Revalidate();
            return true;
        }
        _inFlight.Clear();
        _dirty = false;
        _retries = 0;
        _timer?.Dispose();
        _timer = null;
        Load(s);
        return false;
    }

    void Load(AppSettings s)
    {
        _loading = true;
        try
        {
            var o = s.Overlay;
            int monitor = -1;
            for (int i = 0; i < _deviceNames.Count; i++)
                if (string.Equals(_deviceNames[i], o.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)) monitor = i;
            SelectedMonitorIndex = monitor;
            OffsetX = o.OffsetX;
            OffsetY = o.OffsetY;
            ScalePercent = (int)Math.Round(o.Scale * 100);
            BackgroundOpacityPercent = (int)Math.Round(o.BackgroundOpacity * 100);
            ContentOpacityPercent = (int)Math.Round(o.ContentOpacity * 100);
            ShowPaceLine = o.ShowPaceLine;
            ShowDeltaBar = o.ShowDeltaBar;
            DeltaBarScale = o.DeltaBarScaleLpl;
            RedComplementSeconds = o.RedComplement == RedComplement.Seconds;
            ShowAvgInSupport = o.ShowAvgInSupport;
            AutoHide = o.AutoHide;
            var c = s.Strategy;
            ReserveL = c.ReserveL;
            MeanWindow = c.MeanWindow;
            MaxSavingPercent = Math.Round(c.MaxSavingFraction * 100, 2);
            ExtraLaps = c.ExtraLaps;
            LapsAuto = c.Mode == LapsMode.Auto;
            StintLaps = c.StintLaps;
            ToggleEdit.Text = s.Hotkeys.ToggleEdit;
            ToggleVisible.Text = s.Hotkeys.ToggleVisible;
            DebugMark.Text = s.Hotkeys.DebugMark;
        }
        finally { _loading = false; }
        Revalidate();
        RebuildPreview();
    }

    // ------------------------------------------------------------ edits

    partial void OnSelectedMonitorIndexChanged(int value) => Edited();
    partial void OnOffsetXChanged(int value) => Edited();
    partial void OnOffsetYChanged(int value) => Edited();
    partial void OnShowPaceLineChanged(bool value) => Edited();
    partial void OnShowAvgInSupportChanged(bool value) => Edited();
    partial void OnRedComplementSecondsChanged(bool value) => Edited();

    partial void OnShowDeltaBarChanged(bool value)
    {
        OnPropertyChanged(nameof(DeltaBarScaleEnabled));
        Edited();
    }

    partial void OnDeltaBarScaleChanged(double value)
    {
        // 0.01 steps and the same NaN default as OverlaySettings.Normalize.
        var c = Math.Round(OverlaySettings.Clamp(value, OverlaySettings.MinDeltaBarScale, OverlaySettings.MaxDeltaBarScale, 0.20), 2);
        if (c != value) { DeltaBarScale = c; return; }
        OnPropertyChanged(nameof(DeltaBarScaleText));
        Edited();
    }
    partial void OnAutoHideChanged(bool value) => Edited();
    partial void OnLapsAutoChanged(bool value) => Edited();

    partial void OnScalePercentChanged(int value)
    {
        if (Clamp(value, 50, 250) is var c && c != value) { ScalePercent = c; return; }
        OnPropertyChanged(nameof(PreviewTitle));
        Edited();
    }

    partial void OnBackgroundOpacityPercentChanged(int value)
    {
        if (Clamp(value, 0, 100) is var c && c != value) { BackgroundOpacityPercent = c; return; }
        Edited();
    }

    partial void OnContentOpacityPercentChanged(int value)
    {
        if (Clamp(value, 30, 100) is var c && c != value) { ContentOpacityPercent = c; return; }
        Edited();
    }

    partial void OnReserveLChanged(double value)
    {
        if (Clamp(value, 0, 20) is var c && c != value) { ReserveL = c; return; }
        Edited();
    }

    partial void OnMeanWindowChanged(int value)
    {
        if (Clamp(value, StrategyConfig.MinMeanWindow, StrategyConfig.MaxMeanWindow) is var c && c != value) { MeanWindow = c; return; }
        Edited();
    }

    partial void OnMaxSavingPercentChanged(double value)
    {
        if (Clamp(value, 0, 50) is var c && c != value) { MaxSavingPercent = c; return; }
        Edited();
    }

    partial void OnExtraLapsChanged(double value)
    {
        if (Clamp(value, 0, 10) is var c && c != value) { ExtraLaps = c; return; }
        Edited();
    }

    partial void OnStintLapsChanged(int value)
    {
        if (Clamp(value, 1, 500) is var c && c != value) { StintLaps = c; return; }
        Edited();
    }

    static int Clamp(int v, int min, int max) => Math.Min(max, Math.Max(min, v));
    static double Clamp(double v, double min, double max) => double.IsNaN(v) ? min : Math.Min(max, Math.Max(min, v));

    void OnHotkeyEdited()
    {
        if (_loading) return;
        Revalidate();
        Edited();
    }

    void Edited()
    {
        if (_loading) return;
        _retries = 0;
        _dirty = true;
        RebuildPreview();
        Restart(Debounce);
    }

    void Restart(TimeSpan delay)
    {
        _timer?.Dispose();
        _timer = _sched.Schedule(delay, () => _ui.Post(Flush));
    }

    void Flush()
    {
        _timer = null;
        if (!_out.IsConnected || !_hasSnapshot) return; // never build on a model the Agent did not give us
        var built = Build();
        if (built == (_inFlight.Count > 0 ? _inFlight[^1] : _confirmed)) return;
        _inFlight.Add(built);
        _out.Send(new UpdateSettings(built));
    }

    /// <summary>The fields on top of the latest confirmed settings. A shortcut that is invalid or repeated keeps its confirmed value.</summary>
    AppSettings Build()
    {
        var o = _confirmed.Overlay with
        {
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            Scale = ScalePercent / 100.0,
            BackgroundOpacity = BackgroundOpacityPercent / 100.0,
            ContentOpacity = ContentOpacityPercent / 100.0,
            ShowPaceLine = ShowPaceLine,
            ShowDeltaBar = ShowDeltaBar,
            DeltaBarScaleLpl = DeltaBarScale,
            RedComplement = RedComplementSeconds ? RedComplement.Seconds : RedComplement.Pct,
            ShowAvgInSupport = ShowAvgInSupport,
            AutoHide = AutoHide,
        };
        if (SelectedMonitorIndex >= 0 && SelectedMonitorIndex < _deviceNames.Count)
            o = o with { MonitorDeviceName = _deviceNames[SelectedMonitorIndex] };
        var strategy = _confirmed.Strategy with
        {
            ReserveL = ReserveL,
            MeanWindow = MeanWindow,
            MaxSavingFraction = MaxSavingPercent / 100.0,
            ExtraLaps = ExtraLaps,
            Mode = LapsAuto ? LapsMode.Auto : LapsMode.Stint,
            StintLaps = StintLaps,
        };
        var h = _confirmed.Hotkeys with
        {
            ToggleEdit = Usable(ToggleEdit) ?? _confirmed.Hotkeys.ToggleEdit,
            ToggleVisible = Usable(ToggleVisible) ?? _confirmed.Hotkeys.ToggleVisible,
            DebugMark = Usable(DebugMark) ?? _confirmed.Hotkeys.DebugMark,
        };
        return _confirmed with { Overlay = o, Strategy = strategy, Hotkeys = h };
    }

    static string? Usable(HotkeyFieldViewModel f) => f.Error is null && Hotkey.TryParse(f.Text, out var k) ? k.ToString() : null;

    // ------------------------------------------------------------ shortcuts validation

    void Revalidate()
    {
        var fields = new[] { ToggleEdit, ToggleVisible, DebugMark };
        var keys = new Hotkey?[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            keys[i] = Hotkey.TryParse(fields[i].Text, out var k) ? k : null;
        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            f.Error = keys[i] is not { } mine ? "Atalho inválido. Exemplo: Ctrl+Shift+F"
                : keys.Where((k, j) => j != i && k == mine).Any() ? "Este atalho já é usado por outra ação"
                : null;
            f.Warning = f.Error is null && keys[i] is { } key && _failures.Contains(key.ToString())
                ? "Já em uso por outro programa" : null;
        }
    }

    void RebuildPreview()
    {
        var list = new DrawList();
        var edit = Hotkey.TryParse(ToggleEdit.Text, out var k) ? k.ToString() : HotkeySettings.DefaultToggleEdit;
        var options = new OverlayViewOptions(ScalePercent / 100.0, BackgroundOpacityPercent / 100.0, ContentOpacityPercent / 100.0,
            ShowPaceLine, false, false, edit, 0, 0);
        // The overlay's own builder over the "azul" mock: the Layout options show at once. The reserve stays the mock's
        // (it is not a Layout option, and the sample numbers are the V3-3 ones); the red complement has nothing to show on blue.
        var cfg = GlancePreviews.SampleConfig with
        {
            BarScaleL = DeltaBarScale, ComplementSeconds = RedComplementSeconds, ShowBar = ShowDeltaBar, ShowAvg = ShowAvgInSupport,
        };
        var view = GlanceViewBuilder.Build(in _previewState, in cfg);
        GlanceLayout.Build(in view, options, _measurer, list);
        _preview = list;
        OnPropertyChanged(nameof(Preview));
    }
}
