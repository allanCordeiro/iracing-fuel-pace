using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FuelPace.App.Ipc;
using FuelPace.Core.Capture;
using FuelPace.Core.Storage;

namespace FuelPace.App.SettingsUi.ViewModels;

/// <summary>One line of the profile list: <c>carro · pista · n capturas · confiança</c>.</summary>
public sealed record ProfileItemViewModel(string Key, string Text);

public sealed partial class CaptureItemViewModel : ObservableObject
{
    readonly ProfilesViewModel _owner;
    bool _loading = true;

    internal CaptureItemViewModel(ProfilesViewModel owner, string profileKey, CaptureRecord c, TimeZoneInfo zone)
    {
        _owner = owner;
        ProfileKey = profileKey;
        Id = c.Id;
        Date = DateTimeOffset.TryParse(c.Id, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? TimeZoneInfo.ConvertTime(t, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : c.Id;
        SessionType = c.SessionType;
        var k = c.Conditions;
        Conditions = $"pista {k.TrackTempC.ToString("0", CultureInfo.InvariantCulture)} °C · ar {k.AirTempC.ToString("0", CultureInfo.InvariantCulture)} °C"
            + k.Wetness switch { 1 => " · seca", 2 => " · quase seca", 3 or 4 => " · úmida", >= 5 => " · molhada", _ => "" };
        int valid = c.Laps.Count(l => l.Valid);
        Laps = $"{valid} válidas / {c.Laps.Count}";
        Active = c.Active;
        _loading = false;
    }

    public string ProfileKey { get; }
    public string Id { get; }
    public string Date { get; }
    public string SessionType { get; }
    public string Conditions { get; }
    /// <summary>"6 válidas / 9".</summary>
    public string Laps { get; }

    /// <summary>The toggle <c>ativa</c>; changing it sends <c>setCaptureActive</c>.</summary>
    [ObservableProperty] public partial bool Active { get; set; }

    partial void OnActiveChanged(bool value)
    {
        if (!_loading) _owner.SetActive(this, value);
    }

    /// <summary><c>Apagar</c>: asks for confirmation first.</summary>
    [RelayCommand]
    void Apagar() => _owner.RequestDelete(this);
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    readonly AgentSender _out;
    bool _loading;

    internal ProfilesViewModel(AgentSender sender) => _out = sender;

    public ObservableCollection<ProfileItemViewModel> Items { get; } = [];
    public ObservableCollection<CaptureItemViewModel> Captures { get; } = [];

    /// <summary>The capture dates are the Agent's UTC start times shown in this zone.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    [ObservableProperty] public partial ProfileItemViewModel? SelectedItem { get; set; }

    /// <summary>The capture waiting for the pilot to confirm its deletion.</summary>
    [ObservableProperty] public partial CaptureItemViewModel? PendingDelete { get; private set; }

    partial void OnSelectedItemChanged(ProfileItemViewModel? value)
    {
        Captures.Clear();
        PendingDelete = null;
        if (!_loading && value is not null) _out.Send(new GetProfile(value.Key));
    }

    internal void Refresh() => _out.Send(new ListProfiles());

    internal void OnList(ProfileList list)
    {
        var selected = SelectedItem?.Key;
        _loading = true;
        try
        {
            Items.Clear();
            foreach (var p in list.Items) Items.Add(new ProfileItemViewModel(p.Key, Describe(p)));
            SelectedItem = selected is null ? null : Items.FirstOrDefault(i => i.Key == selected);
        }
        finally { _loading = false; }
        if (SelectedItem is null) Captures.Clear();
    }

    internal void OnProfile(Profile p)
    {
        Refresh(); // counts and confidence changed
        if (SelectedItem?.Key != p.Key) return;
        ProfileDocument? doc;
        try { doc = JsonSerializer.Deserialize(p.Json, ProfileJsonContext.Default.ProfileDocument); }
        catch (JsonException) { return; }
        Captures.Clear();
        PendingDelete = null;
        if (doc is null) return;
        foreach (var c in doc.Captures) Captures.Add(new CaptureItemViewModel(this, p.Key, c, TimeZone));
    }

    internal void SetActive(CaptureItemViewModel c, bool active) =>
        _out.Send(new ProfileCommand(c.ProfileKey, "setCaptureActive", c.Id, null, active));

    internal void RequestDelete(CaptureItemViewModel c) => PendingDelete = c;

    [RelayCommand]
    void ConfirmDelete()
    {
        if (PendingDelete is not { } c) return;
        PendingDelete = null;
        _out.Send(new ProfileCommand(c.ProfileKey, "deleteCapture", c.Id, null, null));
    }

    [RelayCommand]
    void CancelDelete() => PendingDelete = null;

    static string Describe(ProfileSummary p) =>
        $"{p.CarName} · {p.TrackName} · {p.Captures} {(p.Captures == 1 ? "captura" : "capturas")} · "
        + p.Confidence switch { "Alta" => "Confiança alta", "Media" => "Confiança média", "Baixa" => "Confiança baixa", _ => "sem baseline" };
}
