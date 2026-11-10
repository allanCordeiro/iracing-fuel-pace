namespace FuelPace.App.Diagnostics;

/// <summary>Keys of <c>system-info.txt</c> the Agent fills (<c>key: value</c>, one per line).</summary>
public static class SystemInfoKeys
{
    public const string Version = "version";
    public const string Os = "os";
    public const string Dotnet = "dotnet";
    public const string CpuCount = "cpuCount";
    /// <summary>One entry per monitor: name, rectangle and DPI.</summary>
    public const string Monitors = "monitors";
    public const string Gpu = "gpu";
    public const string OverlayBackend = "overlayBackend";
    public const string DebugMode = "debugMode";
    public const string SimWindow = "simWindow";
    public const string FullscreenExclusive = "fullscreenExclusive";
}
