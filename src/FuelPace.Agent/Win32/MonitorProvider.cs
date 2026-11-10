using FuelPace.App.Host;
using FuelPace.App.Placement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace FuelPace.Agent.Win32;

/// <summary>
/// The monitors (<c>EnumDisplayMonitors</c>, <c>GetMonitorInfo</c>, effective DPI from <c>GetDpiForMonitor</c>), in physical
/// pixels (the process is per-monitor DPI aware v2). <see cref="Changed"/> is raised on the UI thread when a display message
/// of the <see cref="MessageWindow"/> (or <see cref="Recheck"/>, e.g. the overlay's own <c>WM_DPICHANGED</c>) finds a list
/// different from the last one, so frequent <c>WM_SETTINGCHANGE</c> broadcasts cost one enumeration and no event.
/// Thread-safe.
/// </summary>
public sealed unsafe class MonitorProvider : IMonitorProvider
{
    private readonly Lock _gate = new();
    private IReadOnlyList<MonitorDesc> _last = [];

    public MonitorProvider(MessageWindow? window = null)
    {
        if (window is not null) window.DisplayMessage += Recheck;
    }

    public event Action? Changed;

    public IReadOnlyList<MonitorDesc> GetMonitors()
    {
        var list = Enumerate();
        lock (_gate) _last = list;
        return list;
    }

    /// <summary>Enumerates again and raises <see cref="Changed"/> when something differs.</summary>
    public void Recheck()
    {
        var list = Enumerate();
        bool changed;
        lock (_gate)
        {
            changed = !list.SequenceEqual(_last);
            _last = list;
        }
        if (changed) Changed?.Invoke();
    }

    public static List<MonitorDesc> Enumerate()
    {
        var list = new List<MonitorDesc>();
        PInvoke.EnumDisplayMonitors(HDC.Null, null, (h, _, _, _) =>
        {
            if (Describe(h) is { } d) list.Add(d);
            return true;
        }, default);
        return list;
    }

    internal static MonitorDesc? Describe(HMONITOR h)
    {
        var mi = new MONITORINFOEXW();
        mi.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!PInvoke.GetMonitorInfo(h, (MONITORINFO*)&mi)) return null;
        double scale = 1.0;
        if (OperatingSystem.IsWindowsVersionAtLeast(10) // the supported OS; tells the analyzer GetDpiForMonitor (8.1+) exists
            && PInvoke.GetDpiForMonitor(h, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded && dpiX > 0)
            scale = dpiX / 96.0;
        return new MonitorDesc(mi.szDevice.ToString(), Rect(mi.monitorInfo.rcMonitor), Rect(mi.monitorInfo.rcWork), scale,
            (mi.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0);
    }

    internal static PixelRect Rect(RECT r) => new(r.left, r.top, r.right - r.left, r.bottom - r.top);
}
