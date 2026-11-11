using System.Globalization;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Host;

/// <summary>
/// The Agent's command line: <c>--debug</c>, <c>--replay &lt;ibt&gt;</c>, <c>--speed max|1|10</c> (replay pace, default 1),
/// <c>--exit-after &lt;s&gt;</c> (smoke tests) and <c>--appdata &lt;dir&gt;</c> (default <c>%APPDATA%\FuelPace</c>).
/// A repeated option keeps its last value.
/// </summary>
public sealed record AgentArgs(bool Debug, string? ReplayPath, ReplaySpeed Speed, TimeSpan? ExitAfter, string? AppData)
{
    public const string Usage = "FuelPace.Agent [--debug] [--replay <ibt>] [--speed max|1|10] [--exit-after <s>] [--appdata <dir>]";

    public static bool TryParse(IReadOnlyList<string> args, out AgentArgs result, out string? error)
    {
        result = new AgentArgs(false, null, ReplaySpeed.X1, null, null);
        error = null;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--debug")
            {
                result = result with { Debug = true };
                continue;
            }
            if (a is not ("--replay" or "--speed" or "--exit-after" or "--appdata"))
            {
                error = $"unknown option {a}";
                return false;
            }
            if (i + 1 >= args.Count || args[i + 1].Length == 0)
            {
                error = $"{a} needs a value";
                return false;
            }
            string v = args[++i];
            switch (a)
            {
                case "--replay":
                    result = result with { ReplayPath = v };
                    break;
                case "--appdata":
                    result = result with { AppData = v };
                    break;
                case "--speed":
                    ReplaySpeed? speed = v.ToLowerInvariant() switch
                    {
                        "max" => ReplaySpeed.Max, "1" => ReplaySpeed.X1, "10" => ReplaySpeed.X10, _ => null,
                    };
                    if (speed is null)
                    {
                        error = $"--speed must be max, 1 or 10 (got {v})";
                        return false;
                    }
                    result = result with { Speed = speed.Value };
                    break;
                default: // --exit-after
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
                        || !double.IsFinite(s) || s < 0 || s > TimeSpan.MaxValue.TotalSeconds / 2)
                    {
                        error = $"--exit-after must be a number of seconds >= 0 (got {v})";
                        return false;
                    }
                    result = result with { ExitAfter = TimeSpan.FromSeconds(s) };
                    break;
            }
        }
        return true;
    }

    /// <param name="defaultAppData">Used without <c>--appdata</c>.</param>
    /// <param name="debugMarker">The <c>FuelPace.debug</c> file is beside the exe.</param>
    public AgentOptions ToOptions(string defaultAppData, bool debugMarker) =>
        new(AppData ?? defaultAppData, Debug, debugMarker, ReplayPath, Speed, ExitAfter);
}
