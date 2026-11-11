namespace FuelPace.App.Placement;

/// <summary>A monitor as reported by the OS. <c>DpiScale</c> = dpi / 96 (1.0, 1.25, 1.5, ...).</summary>
public sealed record MonitorDesc(string DeviceName, PixelRect Bounds, PixelRect WorkArea, double DpiScale, bool IsPrimary);
