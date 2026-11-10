using FuelPace.Core.Telemetry;

namespace FuelPace.App.Recording;

/// <summary>Cuts a recording to the records whose <c>SessionTime</c> is within a window (header, variables and YAML are kept).</summary>
public static class IbtTrimmer
{
    public static void Trim(string input, string output, double fromSessionTime, double toSessionTime)
    {
        if (!(fromSessionTime <= toSessionTime)) throw new ArgumentException("--from must not exceed --to");
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.Ordinal))
            throw new ArgumentException("output must differ from input");

        using var reader = IbtReader.Open(input);
        var layout = VarLayout.Resolve(reader.Variables);
        var slot = layout[TelemetryVarId.SessionTime];
        var row = new byte[reader.Header.BufLen];
        var frame = new TelemetryFrame();
        int kept = 0;
        IbtStreamWriter? writer = null;
        try
        {
            for (int i = 0; i < reader.RecordCount; i++)
            {
                reader.ReadRecord(i, row);
                double t = TelemetryFrameReader.ReadDouble(row, slot);
                if (t < fromSessionTime || t > toSessionTime) continue;
                if (writer is null)
                {
                    writer = new IbtStreamWriter(output, reader.Header.TickRate, Math.Max(reader.Header.SessionInfoLen, 2),
                        includeCarIdx: layout.HasCarIdx, sessionStartDate: reader.DiskHeader.SessionStartDate);
                    writer.WriteSessionInfo(reader.SessionInfoYaml);
                }
                TelemetryFrameReader.Read(row, layout, ref frame);
                writer.Append(in frame);
                kept++;
            }
        }
        finally { writer?.Dispose(); }
        if (kept == 0) throw new ArgumentException($"no records between {fromSessionTime} and {toSessionTime}");
    }
}
