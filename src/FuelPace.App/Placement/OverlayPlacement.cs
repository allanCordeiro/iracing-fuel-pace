using FuelPace.App.Overlay;
using FuelPace.App.Settings;

namespace FuelPace.App.Placement;

/// <summary>
/// Pure placement math. Units: <c>logicalSize</c> is the PANEL size at Scale 1.0 in DIPs, i.e. <see cref="OverlayLayout"/>
/// built with <c>Scale = 1</c> and EditMode off (the layout already multiplies the panel by its own Scale, so passing a
/// layout built at the user's Scale would double-scale). Physical size = logicalSize x OverlaySettings.Scale x
/// monitor.DpiScale. Offsets are physical pixels from the monitor's WorkArea origin. Only the panel is clamped into the
/// work area; edit-mode chrome (banner/footer) may extend slightly beyond it.
/// </summary>
public static class OverlayPlacement
{
    public static PixelRect Resolve(OverlaySettings o, IReadOnlyList<MonitorDesc> monitors, SizeF logicalSize, out MonitorDesc monitor)
    {
        if (monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        monitor = Find(o.MonitorDeviceName, monitors) ?? Primary(monitors);

        double k = Math.Clamp(o.Scale, OverlaySettings.MinScale, OverlaySettings.MaxScale) * monitor.DpiScale;
        int w = Math.Max(1, (int)Math.Round(logicalSize.W * k, MidpointRounding.AwayFromZero));
        int h = Math.Max(1, (int)Math.Round(logicalSize.H * k, MidpointRounding.AwayFromZero));
        var wa = monitor.WorkArea;
        return new PixelRect(Fit(wa.X + o.OffsetX, w, wa.X, wa.Width), Fit(wa.Y + o.OffsetY, h, wa.Y, wa.Height), w, h);
    }

    public static OverlaySettings FromUserMove(OverlaySettings o, PixelRect newRect, IReadOnlyList<MonitorDesc> monitors)
    {
        if (monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        long cx = newRect.X + newRect.Width / 2, cy = newRect.Y + newRect.Height / 2;
        MonitorDesc? m = null;
        foreach (var c in monitors)
            if (cx >= c.Bounds.X && cx < c.Bounds.Right && cy >= c.Bounds.Y && cy < c.Bounds.Bottom) { m = c; break; }
        m ??= Primary(monitors);
        return o with { MonitorDeviceName = m.DeviceName, OffsetX = newRect.X - m.WorkArea.X, OffsetY = newRect.Y - m.WorkArea.Y };
    }

    public static double ScaleFromUserResize(PixelRect newRect, SizeF logicalSize, double dpiScale)
    {
        double denom = logicalSize.W * dpiScale;
        if (denom <= 0) return 1.0;
        return Math.Clamp(newRect.Width / denom, OverlaySettings.MinScale, OverlaySettings.MaxScale);
    }

    /// <summary><paramref name="index"/> is zero-based; shown one-based, e.g. <c>1 · 2560 × 1440</c>.</summary>
    public static string DescribeForUi(MonitorDesc m, int index) => $"{index + 1} · {m.Bounds.Width} × {m.Bounds.Height}";

    static MonitorDesc? Find(string? name, IReadOnlyList<MonitorDesc> ms)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var m in ms)
            if (string.Equals(m.DeviceName, name, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    static MonitorDesc Primary(IReadOnlyList<MonitorDesc> ms)
    {
        foreach (var m in ms) if (m.IsPrimary) return m;
        return ms[0];
    }

    // Keep [pos, pos+size) inside [start, start+extent); if it cannot fit, align to the start.
    static int Fit(int pos, int size, int start, int extent) =>
        size >= extent ? start : Math.Clamp(pos, start, start + extent - size);
}
