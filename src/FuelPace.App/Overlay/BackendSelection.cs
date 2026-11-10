using System.Globalization;
using FuelPace.App.Diagnostics;
using FuelPace.App.Settings;
using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Overlay;

/// <summary>
/// Decision 7: DirectComposition is the main overlay backend and a layered window the automatic fallback
/// (Review Focus 1: an overlay that fails to start composition must still be visible). <c>overlay.renderBackend</c> in
/// settings.json can force either one; forcing composition still falls back, with a warning.
/// </summary>
public static class BackendSelection
{
    /// <summary>
    /// <see cref="RenderBackend.Auto"/> and <see cref="RenderBackend.Composition"/> try <paramref name="tryComposition"/>
    /// (false or an exception = failed) and fall back to <see cref="RenderBackend.Layered"/>; <see cref="RenderBackend.Layered"/>
    /// never tries it. Returns <see cref="RenderBackend.Composition"/> or <see cref="RenderBackend.Layered"/>.
    /// </summary>
    public static RenderBackend Choose(RenderBackend requested, Func<bool> tryComposition, out string reason)
    {
        if (requested == RenderBackend.Layered)
        {
            reason = "layered forced by settings.json (overlay.renderBackend)";
            return RenderBackend.Layered;
        }
        string why;
        try
        {
            if (tryComposition())
            {
                reason = requested == RenderBackend.Composition ? "composition forced by settings.json" : "composition (auto)";
                return RenderBackend.Composition;
            }
            why = "composition failed to start";
        }
        catch (Exception e)
        {
            why = "composition failed to start: " + e.Message;
        }
        reason = requested == RenderBackend.Composition
            ? why + "; settings.json forces composition, using layered instead"
            : why + "; using layered (automatic fallback)";
        return RenderBackend.Layered;
    }

    /// <summary>
    /// Logs the outcome: <c>OverlayBackend</c> (I0 the backend chosen, I1 the HRESULT of the composition failure or 0) and a
    /// text with the reason. Info, or Warn when settings.json forced composition and the overlay runs layered.
    /// </summary>
    public static void Report(DiagLog log, RenderBackend requested, RenderBackend chosen, string reason, int hresult)
    {
        var level = requested == RenderBackend.Composition && chosen != RenderBackend.Composition ? DiagLevel.Warn : DiagLevel.Info;
        log.Emit(level, AppDiagEvents.OverlayBackend, 0, (int)chosen, hresult);
        string hr = hresult == 0 ? "" : " (HRESULT 0x" + hresult.ToString("X8", CultureInfo.InvariantCulture) + ")";
        log.Text(level, "overlay", "overlay backend " + Name(chosen) + ": " + reason + hr);
    }

    /// <summary>The name shown in logs, bundles and the Settings window.</summary>
    public static string Name(RenderBackend b) => b switch
    {
        RenderBackend.Composition => "composition",
        RenderBackend.Layered => "layered",
        _ => "auto",
    };
}
