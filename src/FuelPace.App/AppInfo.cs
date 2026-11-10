using System.Reflection;

namespace FuelPace.App;

public static class AppInfo
{
    public const string DebugMarkerFileName = "FuelPace.debug";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    public static string ExeDirectory { get; } = AppContext.BaseDirectory;

    public static bool DebugMarkerPresent => DebugMarkerPresentIn(ExeDirectory);

    internal static bool DebugMarkerPresentIn(string directory) =>
        File.Exists(Path.Combine(directory, DebugMarkerFileName));
}
