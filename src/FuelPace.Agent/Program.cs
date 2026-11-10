using FuelPace.App;

namespace FuelPace.Agent;

internal static class Program
{
    private static int Main(string[] args)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuelPace", "logs");
        Directory.CreateDirectory(logDir);
        File.AppendAllText(
            Path.Combine(logDir, "agent-startup.log"),
            $"FuelPace.Agent {AppInfo.Version} debugMarker={AppInfo.DebugMarkerPresent}{Environment.NewLine}");

        // --exit-after <seconds>: used by the Windows smoke test.
        var i = Array.IndexOf(args, "--exit-after");
        if (i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
        {
            Thread.Sleep(TimeSpan.FromSeconds(s));
        }
        return 0;
    }
}
