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
            hwnd = createWindow(true);
            try
            {
                renderer = new CompositionRenderer(hwnd, fonts, log);
                return true;
            }
            catch (Exception e)
            {
                hr = e.HResult;
                destroyWindow(hwnd);
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
