using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using FuelPace.App.Ipc;
using FuelPace.Core.Baselines;
using FuelPace.Core.Laps;
using FuelPace.Core.Storage;

namespace FuelPace.App.SettingsUi.ViewModels;

/// <summary>Mock 7: the capture switch, the summary cards and the lap table of the capture in progress (or the last one).</summary>
public sealed partial class CaptureViewModel : ObservableObject
{
    const int IdealLaps = 10;
    const string Dash = "—";

    readonly AgentSender _out;
    List<CapturedLapDto> _laps = [];
    string? _profileKey, _captureId;
    double? _tankL;
    bool _sim, _applying;
    int _valid, _total;

    internal CaptureViewModel(AgentSender sender)
    {
        _out = sender;
        Rebuild();
    }

    // Instance members so the view can bind to them.
    public string ConsumptionTitle => "CONSUMO MÉDIO";
    public string TimeTitle => "TEMPO MÉDIO";
    public string AutonomyTitle => "AUTONOMIA";
    public string BaselineTitle => "BASELINE";
    public string ConsumptionUnit => "L/v";
    public string CaptureLabel => "Capturar voltas";
    public string CaptureHelp => "Começa na próxima passagem pela linha de chegada. Pode fechar esta janela e voltar ao jogo: a captura continua.";

    public ObservableCollection<LapRowViewModel> Laps { get; } = [];

    /// <summary>The lap being driven (<c>em andamento</c>); null when not capturing or before the first lap.</summary>
    [ObservableProperty] public partial LapRowViewModel? InProgress { get; private set; }

    /// <summary>Two-way: a change made by the user sends <c>SetCapture</c>; one that comes from the Agent does not.</summary>
    [ObservableProperty] public partial bool Capturing { get; set; }

    /// <summary>False while the Agent is not reachable: the switch is disabled.</summary>
    [ObservableProperty] public partial bool CanToggle { get; private set; }

    internal void SetOnline(bool online) => CanToggle = online;

    [ObservableProperty] public partial string SessionLine { get; private set; } = "";
    [ObservableProperty] public partial string CounterText { get; private set; } = "";
    [ObservableProperty] public partial string StatusText { get; private set; } = "";

    [ObservableProperty] public partial string ConsumptionValue { get; private set; } = Dash;
    [ObservableProperty] public partial string ConsumptionDetail { get; private set; } = "";
    [ObservableProperty] public partial string TimeValue { get; private set; } = Dash;
    [ObservableProperty] public partial string TimeDetail { get; private set; } = "";
    [ObservableProperty] public partial string AutonomyValue { get; private set; } = Dash;
    [ObservableProperty] public partial string AutonomyUnit { get; private set; } = "v";
    [ObservableProperty] public partial string AutonomyDetail { get; private set; } = "";
    [ObservableProperty] public partial string BaselineValue { get; private set; } = "";
    [ObservableProperty] public partial string BaselineDetail { get; private set; } = "";

    partial void OnCapturingChanged(bool value)
    {
        if (!_applying)
        {
            if (!_out.IsConnected)
            {
                // Offline: the switch cannot do anything, so it goes back instead of lying.
                _applying = true;
                try { Capturing = !value; }
                finally { _applying = false; }
                return;
            }
            _out.Send(new SetCapture(value));
        }
        RefreshState();
    }

    internal void Apply(AgentSnapshot s)
    {
        _profileKey = s.ProfileKey;
        _captureId = s.CaptureId;
        _tankL = s.TankCapacityL;
        _sim = s.SimConnected;
        _laps = [.. s.CaptureLaps];
        _valid = s.Valid;
        _total = s.Total;
        SessionLine = s.CarName is null
            ? s.SimConnected ? "Aguardando uma sessão do iRacing" : "iRacing não está conectado"
            : string.Join(" · ", new[] { s.CarName, s.TrackName, s.SessionType }.Where(x => !string.IsNullOrEmpty(x)));
        SetCapturing(s.CaptureActive, rebuild: true);
    }

    internal void OnLapAdded(CaptureLapAdded m)
    {
        if (m.Total <= 1) { _laps = []; _captureId = null; }
        int i = _laps.FindIndex(l => l.Lap == m.Lap.Lap);
        if (i >= 0) _laps[i] = m.Lap; else _laps.Add(m.Lap);
        _valid = m.Valid;
        _total = m.Total;
        Rebuild();
    }

    internal void OnFinished(CaptureFinished f)
    {
        if (_profileKey is not null && f.ProfileKey == _profileKey) _captureId = f.CaptureId;
        SetCapturing(false, rebuild: false); // the rows stay: the same laps, now editable
    }

    /// <summary>The saved profile after an edit: the table shows the capture it came from.</summary>
    internal void OnProfile(Profile p)
    {
        if (p.Key != _profileKey || _captureId is null) return;
        ProfileDocument? doc;
        try { doc = JsonSerializer.Deserialize(p.Json, ProfileJsonContext.Default.ProfileDocument); }
        catch (JsonException) { return; }
        var rec = doc?.Captures.Find(c => c.Id == _captureId);
        if (rec is null) return;
        _laps = rec.Laps.ConvertAll(CapturedLapDto.From);
        _valid = _laps.Count(l => l.Valid);
        _total = _laps.Count;
        Rebuild();
    }

    void SetCapturing(bool on, bool rebuild)
    {
        _applying = true;
        try
        {
            Capturing = on;
            if (rebuild) Rebuild(); else RefreshState();
        }
        finally { _applying = false; }
    }

    bool CanEditLaps() => _captureId is not null && !Capturing;

    void Rebuild()
    {
        var baseline = ComputeBaseline();
        double economyBelow = baseline is null ? double.NegativeInfinity : baseline.FuelMedianL - baseline.FuelStdL;

        Laps.Clear();
        foreach (var l in _laps) Laps.Add(MakeRow(l, economyBelow));
        FillCards(baseline);
        RefreshState();
    }

    /// <summary>What depends on the capture switch and the saved id, not on the laps: the rows stay the same objects.</summary>
    void RefreshState()
    {
        InProgress = Capturing && _sim && _laps.Count > 0
            ? new LapRowViewModel(_laps[^1].Lap + 1, (_laps[^1].Lap + 1).ToString(CultureInfo.InvariantCulture), "…", "…", "…",
                "em andamento", LapStatusKind.InProgress, "", null, null)
            : null;
        foreach (var row in Laps) row.ProfileCommand.NotifyCanExecuteChanged();
        CounterText = $"{_valid} válidas / {_total}";
        StatusText = Capturing ? "Capturando" : "Parado";
    }

    LapRowViewModel MakeRow(CapturedLapDto l, double economyBelow)
    {
        string status;
        if (l.Valid) status = l.FuelL < economyBelow ? "válida · economia" : "válida";
        else
        {
            var why = ReasonLabels.Format(LapReasonCodes.FromCodes(l.Reasons));
            status = why.Length == 0 ? "inválida" : "inválida · " + why;
        }
        int lap = l.Lap;
        bool exclude = l.Valid;
        return new LapRowViewModel(lap, lap.ToString(CultureInfo.InvariantCulture), LapTimeFormat.Format(l.TimeS),
            LapRowViewModel.FormatFuel(l.FuelL), LapRowViewModel.FormatThrottle(l.FullThrottlePct), status,
            l.Valid ? LapStatusKind.Valid : LapStatusKind.Invalid, exclude ? "Excluir" : "Incluir",
            () =>
            {
                if (_profileKey is { } key && _captureId is { } id)
                    _out.Send(new ProfileCommand(key, "setLapExcluded", id, lap, exclude));
            },
            CanEditLaps);
    }

    Baseline? ComputeBaseline()
    {
        var samples = _laps.Where(l => l.Valid && l.TimeS is not null)
            .Select(l => new LapSample(l.FuelL, l.TimeS!.Value, l.FullThrottlePct, null)).ToList();
        return BaselineCalculator.Compute(samples, _tankL ?? 0, [], DateTimeOffset.UnixEpoch);
    }

    void FillCards(Baseline? b)
    {
        int n = _laps.Count(l => l.Valid && l.TimeS is not null);
        if (b is null)
        {
            ConsumptionValue = TimeValue = AutonomyValue = Dash;
            ConsumptionDetail = TimeDetail = AutonomyDetail = "";
            AutonomyUnit = "v";
            BaselineValue = "Sem baseline";
            BaselineDetail = $"{n} {(n == 1 ? "volta válida" : "voltas válidas")} · mínimo {BaselineCalculator.MinLaps}";
            return;
        }
        ConsumptionValue = LapRowViewModel.FormatFuel(b.FuelMeanL);
        ConsumptionDetail = $"mediana {LapRowViewModel.FormatFuel(b.FuelMedianL)} · mín {LapRowViewModel.FormatFuel(b.FuelMinL)}";
        TimeValue = LapTimeFormat.Format(b.LapMeanS);
        TimeDetail = $"mediana {LapTimeFormat.Format(b.LapMedianS)} · melhor {LapTimeFormat.Format(b.LapBestS)}";
        if (_tankL is > 0 and { } tank)
        {
            AutonomyValue = LapRowViewModel.FormatDecimal(b.AutonomyLaps, 1);
            AutonomyUnit = "v · " + Math.Round((decimal)b.AutonomyMin, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " min";
            AutonomyDetail = "tanque " + tank.ToString("0.#", CultureInfo.InvariantCulture) + " L";
        }
        else
        {
            AutonomyValue = Dash;
            AutonomyUnit = "v";
            AutonomyDetail = "";
        }
        BaselineValue = "Confiança " + b.Confidence switch { Confidence.Alta => "alta", Confidence.Media => "média", _ => "baixa" };
        BaselineDetail = $"{n} {(n == 1 ? "volta válida" : "voltas válidas")} · ideal ≥ {IdealLaps}";
    }
}
