using System.Text.Json;
using FuelPace.App.Diagnostics;
using FuelPace.App.Ipc;
using FuelPace.App.Placement;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Storage;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Host;

// IPC requests from the Settings window (and a second Agent instance), profile commands and diagnostic bundles.
public sealed partial class AgentHost
{
    private static readonly TimeSpan UiRequestTimeout = TimeSpan.FromSeconds(5);
    internal const string SettingsUnreadableText = "configurações ainda não puderam ser lidas; tente novamente";
    internal const string SettingsReloadedText = "configurações recarregadas do arquivo; confira e tente novamente";

    /// <summary>Pool thread, one message at a time per client. State changes run on the UI thread and are awaited, so a
    /// request that follows sees them; disk work goes to the I/O queue and answers from there.</summary>
    private async ValueTask HandleIpcAsync(IpcMessage m, IpcServerContext ctx)
    {
        _log.Emit(DiagLevel.Debug, AppDiagEvents.IpcMessage, SessionTime);
        if (_log.IsEnabled(DiagLevel.Debug)) _log.Text(DiagLevel.Debug, "ipc", $"client {ctx.ClientId}: {m.GetType().Name}");
        switch (m)
        {
            case Hello or GetSnapshot:
                // After a fallback load the Settings window must not start from the defaults: re-read first.
                if (_settingsFallback) await OnUiAsync(() => TryRecoverSettings());
                await ctx.ReplyAsync(new Snapshot(BuildSnapshot()));
                break;
            case SetCapture c:
                SetCapture(c.On);
                break;
            case UpdateSettings u:
                await OnUiAsync(() =>
                {
                    if (_settingsFallback)
                    {
                        // Built from a snapshot of the defaults: never saved over the user's file. If the file is readable
                        // now, its settings are broadcast and the window retries on them.
                        Reply(ctx, new Notice("warn", TryRecoverSettings() ? SettingsReloadedText : SettingsUnreadableText));
                        return;
                    }
                    ApplySettings(u.Settings, save: true);
                });
                break;
            case SetEditMode e:
                await OnUiAsync(() => SetEditMode(e.On));
                break;
            case SetDebugMode d:
                await OnUiAsync(() =>
                {
                    if (!TryRecoverSettings())
                    {
                        Reply(ctx, new Notice("warn", SettingsUnreadableText));
                        return;
                    }
                    ApplySettings(_settings with { Debug = _settings.Debug with { Enabled = d.On } }, save: true);
                });
                break;
            case ListProfiles:
                _io.Enqueue(() => Reply(ctx, ListProfilesNow()));
                break;
            case GetProfile g:
                _io.Enqueue(() => Reply(ctx, GetProfileNow(g.Key)));
                break;
            case ProfileCommand p:
                _io.Enqueue(() => RunProfileCommand(p, ctx));
                break;
            case CreateBundle b:
                _io.Enqueue(() => CreateBundleNow(b.IncludeRecordings, ctx));
                break;
            case ShowSettingsRequest:
                _log.Text(DiagLevel.Info, "app", "another Agent instance asked for the Settings window");
                _sys.LaunchSettings();
                break;
            default:
                _log.Text(DiagLevel.Warn, "ipc", $"client {ctx.ClientId}: unexpected {m.GetType().Name} ignored");
                break;
        }
    }

    private async ValueTask OnUiAsync(Action a)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(() =>
        {
            RunUi(a);
            done.TrySetResult();
        });
        try { await done.Task.WaitAsync(UiRequestTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { _log.Text(DiagLevel.Warn, "ipc", "the UI thread did not handle a request in time"); }
    }

    private void Reply(IpcServerContext ctx, IpcMessage m)
    {
        try { _ = ctx.ReplyAsync(m); }
        catch (Exception e) { _log.Exception("ipc", e); }
    }

    private void Broadcast(IpcMessage m)
    {
        try { _ = _ipc?.BroadcastAsync(m); }
        catch (Exception e) { _log.Exception("ipc", e); }
    }

    private void SetCapture(bool on)
    {
        _log.Text(DiagLevel.Info, "capture", on ? "capture start requested" : "capture stop requested");
        if (!on)
        {
            _pipeline.RequestCaptureStop();
            return;
        }
        if (!_pipeline.CaptureActive)
        {
            using (_captureGate.EnterScope())
            {
                _captureLaps = [];
                _captureValid = 0;
            }
        }
        _pipeline.RequestCaptureStart();
    }

    private AgentSnapshot BuildSnapshot()
    {
        var info = _sessionInfo;
        List<CapturedLapDto> laps;
        int valid;
        using (_captureGate.EnterScope())
        {
            laps = [.. _captureLaps];
            valid = _captureValid;
        }
        var monitors = _monitors;
        var labels = new string[monitors.Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = OverlayPlacement.DescribeForUi(monitors[i], i);
        return new AgentSnapshot(AppInfo.Version, _simConnected, info?.ProfileKey, info?.CarScreenName,
            info is null ? null : TrackLabel(info.TrackDisplayName, info.TrackConfigName),
            info?.FindSession(Volatile.Read(ref _sessionNum))?.Type, _pipeline.CaptureActive, laps, valid, laps.Count,
            _settings, _debugMode, _surface.BackendName, _fullscreen, _hotkeyFailures, labels);
    }

    private static string TrackLabel(string display, string config) =>
        string.IsNullOrEmpty(config) ? display : display + " (" + config + ")";

    // ---------------------------------------------------------------- I/O queue

    /// <summary>A profile key is <c>{carPath}__{trackName}</c> (<see cref="SessionInfo.ProfileKey"/>).</summary>
    private static (string Car, string Track) SplitKey(string key)
    {
        int i = key.IndexOf("__", StringComparison.Ordinal);
        if (i <= 0 || i + 2 >= key.Length) throw new ArgumentException($"invalid profile key '{key}'", nameof(key));
        return (key[..i], key[(i + 2)..]);
    }

    private static string KeyOf(ProfileDocument doc) => doc.Car.Path + "__" + doc.Track.Name;

    private ProfileList ListProfilesNow()
    {
        var items = new List<ProfileSummary>();
        var dir = Path.Combine(_o.AppDataRoot, "profiles");
        if (!Directory.Exists(dir)) return new ProfileList(items);
        foreach (var f in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            int i = name.IndexOf("__", StringComparison.Ordinal);
            if (i <= 0 || name.Contains(".pending-", StringComparison.Ordinal)) continue;
            ProfileDocument? doc;
            try { doc = _profiles.Load(name[..i], name[(i + 2)..]); } // file names are sanitized already; sanitizing again is a no-op
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Text(DiagLevel.Warn, "profile", $"profile {Path.GetFileName(f)} not listed: {e.Message}");
                continue;
            }
            if (doc is null) continue;
            int validLaps = 0;
            foreach (var c in doc.Captures)
                if (c.Active)
                    foreach (var l in c.Laps)
                        if (l.Valid) validLaps++;
            items.Add(new ProfileSummary(KeyOf(doc), doc.Car.Name, TrackLabel(doc.Track.Display, doc.Track.Config),
                doc.Captures.Count, validLaps, doc.Baseline?.Confidence.ToString()));
        }
        return new ProfileList(items);
    }

    private IpcMessage GetProfileNow(string key)
    {
        try
        {
            var (car, track) = SplitKey(key);
            return _profiles.Load(car, track) is { } doc
                ? new Profile(key, JsonSerializer.Serialize(doc, ProfileJsonContext.Default.ProfileDocument))
                : new Error($"Perfil {key} não encontrado.");
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new Error($"Perfil {key} não pôde ser lido: {e.Message}");
        }
    }

    /// <summary>
    /// Ruling A3, on the queue the pipeline saves captures on: load → edit → <see cref="ProfileStore.Save"/> (which refuses a
    /// newer schema) → broadcast the profile → if it is the current session's profile, hand the new document to the
    /// pipeline (the loop does not re-read a profile whose key did not change).
    /// </summary>
    private void RunProfileCommand(ProfileCommand p, IpcServerContext ctx)
    {
        try
        {
            var (car, track) = SplitKey(p.Key);
            var doc = _profiles.Load(car, track) ?? throw new InvalidOperationException($"perfil {p.Key} não encontrado");
            var now = _clock.GetUtcNow();
            switch (p.Op)
            {
                case "setLapExcluded":
                    ProfileOperations.SetLapExcluded(doc, p.CaptureId, p.Lap ?? throw new ArgumentException("lap missing"),
                        p.Value ?? throw new ArgumentException("value missing"), now);
                    break;
                case "setCaptureActive":
                    ProfileOperations.SetCaptureActive(doc, p.CaptureId, p.Value ?? throw new ArgumentException("value missing"), now);
                    break;
                case "deleteCapture":
                    ProfileOperations.DeleteCapture(doc, p.CaptureId, now);
                    break;
                default:
                    throw new ArgumentException($"unknown operation '{p.Op}'");
            }
            _profiles.Save(doc);
            if (p.Op == "deleteCapture") DeletePending(car, track, p.CaptureId);
            _log.Text(DiagLevel.Info, "profile", $"{p.Op} capture {p.CaptureId} lap {p.Lap?.ToString() ?? "-"} value {p.Value?.ToString() ?? "-"} in {p.Key}");
            Broadcast(new Profile(KeyOf(doc), JsonSerializer.Serialize(doc, ProfileJsonContext.Default.ProfileDocument)));
            if (_sessionInfo is { } info && info.ProfileKey == KeyOf(doc)) _pipeline.OnSessionContext(info, doc);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _log.Text(DiagLevel.Warn, "profile", $"{p.Op} on {p.Key} failed: {e.Message}");
            Reply(ctx, new Error("Falha ao alterar o perfil: " + e.Message));
        }
    }

    /// <summary>A deleted capture's pending file would bring it back with the next save of the profile.</summary>
    private void DeletePending(string car, string track, string captureId)
    {
        var path = _profiles.PendingPathFor(car, track, captureId);
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Text(DiagLevel.Warn, "profile", $"pending capture {Path.GetFileName(path)} not deleted: {e.Message}");
        }
    }

    /// <summary>I/O queue: the zip on the desktop (the bundle logs <see cref="AppDiagEvents.BundleCreated"/>); the answer goes to
    /// <paramref name="ctx"/> when an IPC client asked.</summary>
    private string? CreateBundleNow(bool includeRecordings, IpcServerContext? ctx)
    {
        try
        {
            var path = DiagnosticBundle.Create(_o.AppDataRoot, _sys.DesktopDir, new BundleOptions(includeRecordings), SystemInfo(),
                _log, _clock, _sink is { } sink ? sink.FlushNow : null);
            _log.Text(DiagLevel.Info, "app", "diagnostic bundle created: " + Path.GetFileName(path));
            if (ctx is not null) Reply(ctx, new BundleCreated(path));
            return path;
        }
        catch (Exception e)
        {
            _log.Text(DiagLevel.Error, "app", $"diagnostic bundle failed: {e.GetType().Name}: {e.Message}");
            // The exception type only: its message may carry paths under the user's profile.
            if (ctx is not null) Reply(ctx, new Error($"Falha ao gerar o pacote de diagnóstico ({e.GetType().Name})."));
            return null;
        }
    }
}
