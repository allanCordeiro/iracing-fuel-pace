using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FuelPace.App.Host;
using FuelPace.App.Ipc;
using FuelPace.App.Overlay;

namespace FuelPace.App.SettingsUi.ViewModels;

/// <summary>The window: sections, the Agent link and the banners. Owns the four page view models and routes what the Agent sends.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    public const string WindowTitle = "FuelPace · Configurações";
    public const string OfflineText = "Agent não está rodando";
    static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    readonly IAgentConnection _conn;
    readonly ISynchronizationContext _ui;
    readonly IDelayScheduler _sched;
    readonly AgentSender _sender;
    IDisposable? _retry;
    bool _disposed, _connecting;

    public ShellViewModel(IAgentConnection connection, ISynchronizationContext ui, IDelayScheduler scheduler, IShellServices shell,
        ITextMeasurer? measurer = null)
    {
        _conn = connection;
        _ui = ui;
        _sched = scheduler;
        var sender = _sender = new AgentSender(connection);
        Capture = new CaptureViewModel(sender);
        Overlay = new OverlayViewModel(sender, ui, scheduler, measurer);
        Profiles = new ProfilesViewModel(sender);
        Diagnostics = new DiagnosticsViewModel(sender, shell);
        _conn.Received += OnReceived;
        _conn.Disconnected += OnDisconnected;
    }

    public CaptureViewModel Capture { get; }
    public OverlayViewModel Overlay { get; }
    public ProfilesViewModel Profiles { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    public string Title => WindowTitle;
    public IReadOnlyList<string> Sections { get; } = ["Captura", "Overlay e cálculo", "Perfis", "Diagnóstico"];
    [ObservableProperty] public partial string SelectedSection { get; set; } = "Captura";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentOfflineBanner))]
    public partial bool AgentOnline { get; private set; }

    public bool ShowAgentOfflineBanner => !AgentOnline;
    public string AgentOfflineText => OfflineText;

    [ObservableProperty] public partial bool ShowFullscreenBanner { get; private set; }
    public string FullscreenText => AgentHost.FullscreenText;

    /// <summary>The last message the Agent sent that is not about settings (a failed profile edit, a failed bundle).</summary>
    [ObservableProperty] public partial string? Message { get; private set; }

    /// <summary>Connects and asks for the state; while the Agent is not there it keeps trying every 2 s.</summary>
    public async Task ConnectAsync()
    {
        if (_disposed || _connecting) return;
        _connecting = true;
        _retry?.Dispose();
        _retry = null;
        bool ok;
        try { ok = await _conn.ConnectAsync(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException) { ok = false; }
        finally { _connecting = false; }
        AgentOnline = ok;
        Capture.SetOnline(ok);
        if (!ok) { ScheduleReconnect(); return; }
        _sender.Send(new Hello(AppInfo.Version));
    }

    [RelayCommand]
    void IniciarAgent()
    {
        _conn.LaunchAgent();
        if (!AgentOnline && _retry is null) ScheduleReconnect();
    }

    void ScheduleReconnect()
    {
        if (_disposed) return;
        _retry?.Dispose();
        _retry = _sched.Schedule(ReconnectDelay, () => _ui.Post(() => _ = ConnectAsync()));
    }

    void OnReceived(IpcMessage m) => _ui.Post(() => Dispatch(m));

    void OnDisconnected() => _ui.Post(() =>
    {
        if (_disposed) return;
        AgentOnline = false;
        Capture.SetOnline(false);
        Overlay.OnDisconnected();
        ScheduleReconnect();
    });

    void Dispatch(IpcMessage m)
    {
        if (_disposed) return;
        switch (m)
        {
            case Snapshot s:
                AgentOnline = true;
                Capture.SetOnline(true);
                ShowFullscreenBanner = s.S.FullscreenExclusive;
                Message = null;
                Capture.Apply(s.S);
                Overlay.Apply(s.S);
                Diagnostics.Apply(s.S);
                Profiles.Refresh();
                break;
            case CaptureLapAdded a:
                Capture.OnLapAdded(a);
                break;
            case CaptureFinished f:
                Capture.OnFinished(f);
                Profiles.Refresh();
                break;
            case ProfileList l:
                Profiles.OnList(l);
                break;
            case Profile p:
                Capture.OnProfile(p);
                Profiles.OnProfile(p);
                break;
            case SettingsApplied a:
                Overlay.OnSettingsApplied(a);
                break;
            case BundleCreated b:
                Diagnostics.OnBundleCreated(b);
                break;
            case Notice n when n.Text == AgentHost.FullscreenText:
                ShowFullscreenBanner = true; // the banner says it
                break;
            case Notice n:
                if (!Overlay.OnNotice(n)) Message = n.Text;
                break;
            case FullscreenChanged f:
                ShowFullscreenBanner = f.On;
                break;
            case SessionChanged s:
                Capture.OnSession(s);
                break;
            case Error e:
                Message = e.Text;
                Diagnostics.OnError();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Received -= OnReceived;
        _conn.Disconnected -= OnDisconnected;
        _retry?.Dispose();
    }
}
