using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FuelPace.App.SettingsUi.ViewModels;

public enum LapStatusKind { Valid, Invalid, InProgress }

/// <summary>One row of the capture table: VOLTA, TEMPO, CONSUMO, PÉ EMBAIXO, STATUS, AÇÃO.</summary>
public sealed class LapRowViewModel : ObservableObject
{
    public int Lap { get; }
    public string LapText { get; }
    public string Time { get; }
    public string Fuel { get; }
    public string FullThrottle { get; }
    public string Status { get; }
    public LapStatusKind Kind { get; }
    /// <summary><c>Excluir</c> (valid lap), <c>Incluir</c> (invalid lap) or empty (in progress).</summary>
    public string ActionText { get; }
    /// <summary>Sends <c>ProfileCommand</c> (setLapExcluded); disabled until the capture is saved.</summary>
    public RelayCommand ProfileCommand { get; }

    internal LapRowViewModel(int lap, string lapText, string time, string fuel, string throttle, string status, LapStatusKind kind,
        string actionText, Action? execute, Func<bool>? canExecute)
    {
        Lap = lap;
        LapText = lapText;
        Time = time;
        Fuel = fuel;
        FullThrottle = throttle;
        Status = status;
        Kind = kind;
        ActionText = actionText;
        ProfileCommand = new RelayCommand(() => execute?.Invoke(), () => execute is not null && (canExecute?.Invoke() ?? true));
    }

    internal static string FormatFuel(double l) => FormatDecimal(l, 3);

    internal static string FormatDecimal(double v, int places) =>
        Math.Round((decimal)v, places, MidpointRounding.AwayFromZero).ToString("0." + new string('0', places), CultureInfo.InvariantCulture);

    internal static string FormatThrottle(double pct) =>
        double.IsNaN(pct) ? "—" : Math.Round(pct, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
}
