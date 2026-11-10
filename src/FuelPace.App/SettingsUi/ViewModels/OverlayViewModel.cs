using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FuelPace.App.Host;
using FuelPace.App.Ipc;
using FuelPace.App.Overlay;
using FuelPace.App.Settings;
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
    readonly OverlayState _previewState = OverlayPreviews.All.First(p => p.Name == "azul").State;

    AppSettings _confirmed = new();
    AppSettings? _lastSent;
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
    [ObservableProperty] public partial bool ShowInsightLine { get; set; }
    /// <summary>"Ocultar fora da pista".</summary>
    [ObservableProperty] public partial bool AutoHide { get; set; }

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
        _debugMode = s.DebugMode;
        OnPropertyChanged(nameof(ShowDebugMark));
        _deviceNames = s.MonitorDeviceNames ?? [];
        _failures = s.HotkeyFailures;
        MonitorLabels.Clear();
        foreach (var l in s.MonitorLabels) MonitorLabels.Add(l);
        StatusMessage = null;
        ApplyRemote(s.Settings);
    }

    internal void OnSettingsApplied(SettingsApplied m)
    {
        if (ApplyRemote(m.Settings)) StatusMessage = null;
    }

    internal bool OnNotice(Notice n)
    {
        if (n.Text == AgentHost.SettingsUnreadableText)
        {
            _lastSent = null; // the Agent did not take it: the next flush sends again
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
        if (_lastSent is not null && s == _lastSent)
        {
            _lastSent = null;
            _retries = 0;
            Revalidate();
            return true;
        }
        _lastSent = null;
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
            ShowInsightLine = o.ShowInsightLine;
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
    partial void OnShowInsightLineChanged(bool value) => Edited();
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
        if (!_out.IsConnected) return;
        var built = Build();
        if (built == (_lastSent ?? _confirmed)) return;
        _lastSent = built;
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
            ShowInsightLine = ShowInsightLine,
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
            ShowPaceLine, ShowInsightLine, false, false, edit, 0, 0);
        OverlayLayout.Build(in _previewState, OverlayPreviews.Car, OverlayPreviews.Track, options, _measurer, list);
        _preview = list;
        OnPropertyChanged(nameof(Preview));
    }
}
