using System.Text.Json.Serialization;
using FuelPace.App.Settings;
using FuelPace.Core.Capture;
using FuelPace.Core.Laps;

namespace FuelPace.App.Ipc;

/// <summary>Base of every message on the Agent/Settings pipe. JSON discriminator: <c>"type"</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(GetSnapshot), "getSnapshot")]
[JsonDerivedType(typeof(SetCapture), "setCapture")]
[JsonDerivedType(typeof(UpdateSettings), "updateSettings")]
[JsonDerivedType(typeof(SetEditMode), "setEditMode")]
[JsonDerivedType(typeof(ListProfiles), "listProfiles")]
[JsonDerivedType(typeof(GetProfile), "getProfile")]
[JsonDerivedType(typeof(ProfileCommand), "profileCommand")]
[JsonDerivedType(typeof(CreateBundle), "createBundle")]
[JsonDerivedType(typeof(SetDebugMode), "setDebugMode")]
[JsonDerivedType(typeof(ShowSettingsRequest), "showSettingsRequest")]
[JsonDerivedType(typeof(Snapshot), "snapshot")]
[JsonDerivedType(typeof(CaptureLapAdded), "captureLapAdded")]
[JsonDerivedType(typeof(CaptureFinished), "captureFinished")]
[JsonDerivedType(typeof(ProfileList), "profileList")]
[JsonDerivedType(typeof(Profile), "profile")]
[JsonDerivedType(typeof(SettingsApplied), "settingsApplied")]
[JsonDerivedType(typeof(BundleCreated), "bundleCreated")]
[JsonDerivedType(typeof(Notice), "notice")]
[JsonDerivedType(typeof(FullscreenChanged), "fullscreenChanged")]
[JsonDerivedType(typeof(SessionChanged), "sessionChanged")]
[JsonDerivedType(typeof(Error), "error")]
public abstract record IpcMessage;

// Settings -> Agent
public sealed record Hello(string Version) : IpcMessage;
public sealed record GetSnapshot : IpcMessage;
public sealed record SetCapture(bool On) : IpcMessage;
public sealed record UpdateSettings(AppSettings Settings) : IpcMessage;
public sealed record SetEditMode(bool On) : IpcMessage;
public sealed record ListProfiles : IpcMessage;
public sealed record GetProfile(string Key) : IpcMessage;

/// <param name="Op">"setLapExcluded" | "setCaptureActive" | "deleteCapture"</param>
public sealed record ProfileCommand(string Key, string Op, string CaptureId, int? Lap, bool? Value) : IpcMessage;
public sealed record CreateBundle(bool IncludeRecordings) : IpcMessage;
public sealed record SetDebugMode(bool On) : IpcMessage;
/// <summary>Sent by a second Agent instance so the first one opens the Settings window.</summary>
public sealed record ShowSettingsRequest : IpcMessage;

// Agent -> Settings
public sealed record Snapshot(AgentSnapshot S) : IpcMessage;
public sealed record CaptureLapAdded(CapturedLapDto Lap, int Valid, int Total) : IpcMessage;
public sealed record CaptureFinished(string ProfileKey, string CaptureId) : IpcMessage;
public sealed record ProfileList(IReadOnlyList<ProfileSummary> Items) : IpcMessage;
public sealed record Profile(string Key, string Json) : IpcMessage;
public sealed record SettingsApplied(AppSettings Settings) : IpcMessage;
public sealed record BundleCreated(string Path) : IpcMessage;
public sealed record Notice(string Level, string Text) : IpcMessage;
/// <summary>The sim went into or out of exclusive fullscreen (the Settings window's banner follows it).</summary>
public sealed record FullscreenChanged(bool On) : IpcMessage;
/// <summary>The sim connected or went away, or its session (car, track, session type) changed: the same fields as the
/// <see cref="AgentSnapshot"/>, sent to open Settings windows only when one of them changes.</summary>
public sealed record SessionChanged(bool SimConnected, string? ProfileKey, string? CarName, string? TrackName, string? SessionType,
    double? TankCapacityL) : IpcMessage;
public sealed record Error(string Text) : IpcMessage;

/// <summary>Wire form of a captured lap; reasons as <see cref="LapReasonCodes"/>. NaN doubles travel as null.</summary>
public sealed record CapturedLapDto(int Lap, double? TimeS, double FuelL, double FullThrottlePct, string[] Reasons, bool Valid)
{
    public static CapturedLapDto From(CapturedLap l) =>
        new(l.Lap, l.TimeS, l.FuelL, l.FullThrottlePct, LapReasonCodes.ToCodes(l.Reasons), l.Valid);
}

/// <param name="Confidence">"Baixa" | "Media" | "Alta"; null when the profile has no baseline.</param>
public sealed record ProfileSummary(string Key, string CarName, string TrackName, int Captures, int ValidLaps, string? Confidence);

/// <param name="TankCapacityL">Of the current car (null without a session).</param>
/// <param name="CaptureId">Id of the capture that last finished in this session, the one the lap table can edit; null while a capture runs.</param>
/// <param name="MonitorDeviceNames">GDI device name of each entry of <paramref name="MonitorLabels"/>, same order (what <c>OverlaySettings.MonitorDeviceName</c> holds).</param>
/// <param name="DebugForcedBy">"marker" (the <c>FuelPace.debug</c> file) or "commandLine" (<c>--debug</c>) when debug mode is on
/// whatever the Settings toggle says; null when the toggle decides.</param>
public sealed record AgentSnapshot(
    string Version, bool SimConnected, string? ProfileKey, string? CarName, string? TrackName, string? SessionType,
    bool CaptureActive, IReadOnlyList<CapturedLapDto> CaptureLaps, int Valid, int Total, AppSettings Settings,
    bool DebugMode, string OverlayBackend, bool FullscreenExclusive,
    IReadOnlyList<string> HotkeyFailures, IReadOnlyList<string> MonitorLabels,
    double? TankCapacityL = null, string? CaptureId = null, IReadOnlyList<string>? MonitorDeviceNames = null,
    string? DebugForcedBy = null);
