using CommunityToolkit.Mvvm.ComponentModel;

namespace FuelPace.App.SettingsUi.ViewModels;

/// <summary>One shortcut: its text, the validation error and whether the Agent failed to register it (both show in red).</summary>
public sealed partial class HotkeyFieldViewModel : ObservableObject
{
    readonly Action _edited;

    internal HotkeyFieldViewModel(string label, Action edited)
    {
        Label = label;
        _edited = edited;
    }

    public string Label { get; }

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string? Error { get; internal set; }
    [ObservableProperty] public partial string? Warning { get; internal set; }

    public bool IsRed => Error is not null || Warning is not null;

    partial void OnTextChanged(string value) => _edited();
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(IsRed));
    partial void OnWarningChanged(string? value) => OnPropertyChanged(nameof(IsRed));
}
