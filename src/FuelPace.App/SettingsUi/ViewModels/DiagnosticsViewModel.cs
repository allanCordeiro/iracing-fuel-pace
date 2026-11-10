using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FuelPace.App.Ipc;

namespace FuelPace.App.SettingsUi.ViewModels;

public sealed partial class DiagnosticsViewModel : ObservableObject
{
    readonly AgentSender _out;
    readonly IShellServices _shell;
    bool _applying;

    internal DiagnosticsViewModel(AgentSender sender, IShellServices shell)
    {
        _out = sender;
        _shell = shell;
    }

    [ObservableProperty] public partial string Version { get; private set; } = AppInfo.Version;
    [ObservableProperty] public partial string OverlayBackend { get; private set; } = "";

    /// <summary>"Modo debug": a change made by the user sends <c>SetDebugMode</c>.</summary>
    [ObservableProperty] public partial bool DebugMode { get; set; }

    /// <summary>"Incluir gravações" in the bundle.</summary>
    [ObservableProperty] public partial bool IncludeRecordings { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GerarPacoteCommand))]
    public partial bool Busy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AbrirPastaDoPacoteCommand))]
    public partial string? BundlePath { get; private set; }

    partial void OnDebugModeChanged(bool value)
    {
        if (!_applying) _out.Send(new SetDebugMode(value));
    }

    internal void Apply(AgentSnapshot s)
    {
        Version = s.Version;
        OverlayBackend = s.OverlayBackend;
        _applying = true;
        try { DebugMode = s.DebugMode; }
        finally { _applying = false; }
    }

    internal void OnBundleCreated(BundleCreated m)
    {
        BundlePath = m.Path;
        Busy = false;
    }

    internal void OnError() => Busy = false;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    void GerarPacote()
    {
        Busy = true;
        BundlePath = null;
        _out.Send(new CreateBundle(IncludeRecordings));
    }

    bool CanGenerate() => !Busy;

    [RelayCommand(CanExecute = nameof(HasBundle))]
    void AbrirPastaDoPacote()
    {
        if (BundlePath is { } p && Path.GetDirectoryName(p) is { Length: > 0 } dir) _shell.OpenFolder(dir);
    }

    bool HasBundle() => BundlePath is not null;

    [RelayCommand]
    void AbrirPastaDeLogs() => _shell.OpenFolder(_shell.LogsFolder);
}
