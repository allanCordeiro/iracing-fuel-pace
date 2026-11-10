using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FuelPace.App.Diagnostics;
using FuelPace.App.Placement;
using Vortice.DXGI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FuelPace.Agent.Win32;

/// <summary>
/// The Agent's part of <c>system-info.txt</c> (<see cref="SystemInfoKeys"/>: OS, .NET, CPUs, monitors, GPUs, the iRacing
/// window); the host adds version, debug mode, overlay backend and fullscreen. Nothing here names the user or a path under the
/// user profile (bundles may be shared): device names, adapter descriptions, window styles and rectangles only. Each part that
/// fails reports <c>error: &lt;type&gt;</c> instead. Stateless and thread-safe.
/// </summary>
public static class SystemInfoCollector
{
    /// <summary>The iRacing simulator's process (DirectX 11 build).</summary>
    public const string SimProcessName = "iRacingSim64DX11";

    public static Dictionary<string, string> Collect() => new()
    {
        [SystemInfoKeys.Os] = Safe(() => $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}"),
        [SystemInfoKeys.Dotnet] = Safe(() => $"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}"),
        [SystemInfoKeys.CpuCount] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
        [SystemInfoKeys.Monitors] = Safe(Monitors),
        [SystemInfoKeys.Gpu] = Safe(Gpus),
        [SystemInfoKeys.SimWindow] = Safe(SimWindow),
    };

    internal static string Monitors()
    {
        var list = MonitorProvider.Enumerate();
        if (list.Count == 0) return "none";
        return string.Join("; ", list.Select(m => Inv(
            $"{m.DeviceName} {Rect(m.Bounds)} work {Rect(m.WorkArea)} scale {m.DpiScale:0.##}{(m.IsPrimary ? " primary" : "")}")));
    }

    /// <summary>Every DXGI adapter: description, dedicated video memory, and whether it is the software renderer.</summary>
    internal static string Gpus()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var parts = new List<string>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
        {
            using (adapter)
            {
                var d = adapter!.Description1;
                bool software = (d.Flags & AdapterFlags.Software) != 0;
                parts.Add(Inv($"{d.Description.Trim()} ({(ulong)d.DedicatedVideoMemory / (1024 * 1024)} MB{(software ? ", software" : "")})"));
            }
        }
        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    /// <summary>The sim's main window: styles, rectangle and the monitor it is on (borderless covers the monitor, no caption).</summary>
    internal static unsafe string SimWindow()
    {
        var procs = Process.GetProcessesByName(SimProcessName);
        try
        {
            if (procs.Length == 0) return "not running";
            var hwnd = (HWND)(void*)procs[0].MainWindowHandle;
            if (hwnd.IsNull || !PInvoke.IsWindow(hwnd)) return "running, no main window";
            uint style = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            uint exStyle = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if (!PInvoke.GetWindowRect(hwnd, out RECT r)) return Inv($"style 0x{style:X8} exStyle 0x{exStyle:X8}");
            var rect = MonitorProvider.Rect(r);
            string monitor = "?";
            bool covers = false;
            var h = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            if (MonitorProvider.Describe(h) is { } m)
            {
                monitor = m.DeviceName;
                covers = rect.X <= m.Bounds.X && rect.Y <= m.Bounds.Y && rect.Right >= m.Bounds.Right && rect.Bottom >= m.Bounds.Bottom;
            }
            bool caption = (style & (uint)WINDOW_STYLE.WS_CAPTION) == (uint)WINDOW_STYLE.WS_CAPTION;
            return Inv($"style 0x{style:X8} exStyle 0x{exStyle:X8} rect {Rect(rect)} monitor {monitor}")
                + $" coversMonitor {(covers ? "yes" : "no")} caption {(caption ? "yes" : "no")}";
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static string Rect(PixelRect r) => Inv($"{r.X},{r.Y} {r.Width}x{r.Height}");

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static string Safe(Func<string> f)
    {
        try { return f(); }
        catch (Exception e) { return "error: " + e.GetType().Name; }
    }
}
