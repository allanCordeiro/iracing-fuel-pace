using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace FuelPace.Core.Telemetry;

/// <summary>Parses the iRacing session-info YAML. Runs off the hot path (allocations are fine).</summary>
public static partial class SessionInfoParser
{
    [GeneratedRegex(@"^(\s*(?:- )?[A-Za-z0-9_]+: )(.+?)\s*$")]
    private static partial Regex KeyValueLine();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Normalizes line endings and single-quotes every unquoted scalar value so that
    /// colons, hashes and quotes inside iRacing names do not break the YAML parser.</summary>
    public static string Sanitize(string yaml)
    {
        var lines = yaml.Replace("\0", "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = KeyValueLine().Match(lines[i]);
            if (!m.Success) continue;
            var value = m.Groups[2].Value;
            if (value[0] == '\'' || value[0] == '"') continue;
            lines[i] = m.Groups[1].Value + "'" + value.Replace("'", "''") + "'";
        }
        return string.Join('\n', lines);
    }

    public static SessionInfo Parse(string yaml)
    {
        Root? root;
        try { root = Deserializer.Deserialize<Root>(Sanitize(yaml)); }
        catch (YamlException ex) { throw new FormatException("Session info YAML is malformed: " + ex.Message, ex); }

        var weekend = root?.WeekendInfo ?? throw new FormatException("Session info YAML has no WeekendInfo section.");
        var di = root.DriverInfo ?? throw new FormatException("Session info YAML has no DriverInfo section.");

        int idx = (int)(LeadingNumber(di.DriverCarIdx) ?? throw new FormatException("DriverInfo.DriverCarIdx is missing."));
        DriverDto? me = null;
        if (di.Drivers != null)
            foreach (var d in di.Drivers)
                if ((int?)LeadingNumber(d.CarIdx) == idx) { me = d; break; }
        if (me == null) throw new FormatException($"DriverInfo.Drivers has no entry for DriverCarIdx {idx}.");

        var carScreen = me.CarScreenName ?? "";
        var trackDisplay = weekend.TrackDisplayName ?? "";
        var maxLtr = LeadingNumber(di.DriverCarFuelMaxLtr);
        var pct = LeadingNumber(di.DriverCarMaxFuelPct) ?? 1.0;
        double tank = maxLtr.HasValue ? maxLtr.Value * pct : double.NaN;

        var sessions = new List<SessionDef>();
        if (root.SessionInfo?.Sessions != null)
            foreach (var s in root.SessionInfo.Sessions)
            {
                var num = LeadingNumber(s.SessionNum);
                if (!num.HasValue) continue;
                var laps = LeadingNumber(s.SessionLaps);
                sessions.Add(new SessionDef((int)num.Value, s.SessionType ?? "",
                    laps.HasValue ? (int)laps.Value : null, LeadingNumber(s.SessionTime)));
            }

        return new SessionInfo(
            idx,
            me.CarPath ?? "",
            carScreen,
            string.IsNullOrEmpty(me.CarScreenNameShort) ? carScreen : me.CarScreenNameShort,
            weekend.TrackName ?? "",
            string.IsNullOrEmpty(weekend.TrackDisplayShortName) ? trackDisplay : weekend.TrackDisplayShortName,
            trackDisplay,
            weekend.TrackConfigName ?? "",
            LeadingNumber(weekend.TrackLength) ?? double.NaN,
            tank,
            LeadingNumber(di.DriverCarFuelKgPerLtr) ?? double.NaN,
            LeadingNumber(di.DriverCarEstLapTime) ?? double.NaN,
            sessions);
    }

    /// <summary>"3.9342 km" → 3.9342; "unlimited"/empty/null → null.</summary>
    private static double? LeadingNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var span = s.AsSpan().Trim();
        int end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] is '.' or '-' or '+')) end++;
        if (end == 0) return null;
        return double.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private sealed class Root
    {
        public WeekendDto? WeekendInfo { get; set; }
        public SessionInfoDto? SessionInfo { get; set; }
        public DriverInfoDto? DriverInfo { get; set; }
    }

    private sealed class WeekendDto
    {
        public string? TrackName { get; set; }
        public string? TrackLength { get; set; }
        public string? TrackDisplayName { get; set; }
        public string? TrackDisplayShortName { get; set; }
        public string? TrackConfigName { get; set; }
    }

    private sealed class SessionInfoDto { public List<SessionDto>? Sessions { get; set; } }

    private sealed class SessionDto
    {
        public string? SessionNum { get; set; }
        public string? SessionLaps { get; set; }
        public string? SessionTime { get; set; }
        public string? SessionType { get; set; }
    }

    private sealed class DriverInfoDto
    {
        public string? DriverCarIdx { get; set; }
        public string? DriverCarFuelMaxLtr { get; set; }
        public string? DriverCarMaxFuelPct { get; set; }
        public string? DriverCarFuelKgPerLtr { get; set; }
        public string? DriverCarEstLapTime { get; set; }
        public List<DriverDto>? Drivers { get; set; }
    }

    private sealed class DriverDto
    {
        public string? CarIdx { get; set; }
        public string? CarPath { get; set; }
        public string? CarScreenName { get; set; }
        public string? CarScreenNameShort { get; set; }
    }
}
