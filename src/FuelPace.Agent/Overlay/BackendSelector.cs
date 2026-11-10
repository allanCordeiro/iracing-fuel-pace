using FuelPace.App.Diagnostics;
using FuelPace.App.Overlay;
using FuelPace.App.Settings;
using FuelPace.Core.Diagnostics;

namespace FuelPace.Agent.Overlay;

/// <summary>What draws into the overlay window (UI thread).</summary>
internal interface IOverlayRenderer : IDisposable
{
    /// <summary>The window's new size in physical pixels.</summary>
    void Resize(int width, int height);

    /// <summary>Draws <paramref name="list"/> scaled by <paramref name="scale"/> (1, except while a corner is dragged) and
    /// shows it. <paramref name="hitTestable"/>: edit mode, where every pixel of the window must take the mouse.</summary>
    void Render(DrawList list, float scale, bool hitTestable);
}

/// <summary>
/// Logs a failed frame as <c>RenderFailed</c> (I0 stage, I1 HRESULT) once per distinct (stage, HRESULT) until a frame
/// succeeds, so a persistent failure at 10 redraws/s does not flood the log. Also throttles device rebuilds to one a second.
/// </summary>
internal struct RenderFailures
{
    public const int StageDraw = 0, StagePresent = 1, StageCommit = 2, StageShow = 3, StageRebuild = 4, StageResize = 5;
    private const long RetryMs = 1000;

    private long _last; // stage << 32 | hresult; 0 = none since the last success
    private long _lastAttempt;

    public void Report(DiagLog log, int stage, int hresult, string? detail = null)
    {
        long key = ((long)stage << 32) | (uint)hresult;
        if (key == _last) return;
        _last = key;
        log.Emit(DiagLevel.Error, AppDiagEvents.RenderFailed, 0, stage, hresult);
        if (detail is not null) log.Text(DiagLevel.Error, "overlay", detail);
    }

    public void Succeeded() => _last = 0;

    /// <summary>True at most once a second (device rebuilds while a GPU reset or driver update is in progress).</summary>
    public bool MayRetry()
    {
        long now = Environment.TickCount64;
        if (_lastAttempt != 0 && now - _lastAttempt < RetryMs) return false;
        _lastAttempt = now;
        return true;
    }
}

/// <summary>
/// The Windows side of <see cref="BackendSelection"/>: composition gets a window with <c>WS_EX_NOREDIRECTIONBITMAP</c> (it
/// must be set at creation, so a failed attempt destroys that window) and a <see cref="CompositionRenderer"/>; the layered
/// fallback a plain layered window and a <see cref="LayeredRenderer"/>.
/// </summary>
internal static class BackendSelector
{
    /// <param name="createWindow">Creates the overlay window; the argument is true for composition.</param>
    /// <param name="destroyWindow">Destroys a window whose composition renderer failed.</param>
    /// <param name="hresult">The composition failure's HRESULT, or 0.</param>
    public static (RenderBackend Backend, nint Hwnd, IOverlayRenderer Renderer) Create(RenderBackend requested,
        Func<bool, nint> createWindow, Action<nint> destroyWindow, DWriteFonts fonts, Func<DiagLog>? log,
        out string reason, out int hresult)
    {
        nint hwnd = 0;
        IOverlayRenderer? renderer = null;
        int hr = 0;
        var backend = BackendSelection.Choose(requested, () =>
        {
            try
            {
                hwnd = createWindow(true);
                renderer = new CompositionRenderer(hwnd, fonts, log);
                return true;
            }
            catch (Exception e)
            {
                hr = e.HResult; // a CreateWindowEx failure too (Win32Exception carries it)
                if (hwnd != 0) destroyWindow(hwnd);
                hwnd = 0;
                throw;
            }
        }, out reason);
        hresult = hr;
        if (backend == RenderBackend.Composition)
        {
            if (renderer is CompositionRenderer { Hardware: false })
                reason += "; no hardware D3D11 device, drawing on WARP (software)";
            return (backend, hwnd, renderer!);
        }

        hwnd = createWindow(false);
        try
        {
            return (RenderBackend.Layered, hwnd, new LayeredRenderer(hwnd, fonts, log));
        }
        catch
        {
            destroyWindow(hwnd);
            throw;
        }
    }
}
