using System.Globalization;
using System.Text.Json;
using FuelPace.Core.Laps;

namespace FuelPace.Core.Diagnostics;

public static class DiagJsonFormatter
{
    public static void Write(Utf8JsonWriter w, in DiagRecord r)
    {
        var info = DiagEventCatalog.Get(r.Event);
        w.WriteStartObject();
        w.WriteString("t", Iso(r.WallTicksUtc));
        w.WriteNumber("seq", r.Seq);
        WriteDouble(w, "st", r.SessionTime);
        w.WriteString("lvl", LevelName(r.Level));
        w.WriteString("cat", info.Category);
        w.WriteString("ev", info.Name);
        if (info.I0 is not null) w.WriteNumber(info.I0, r.I0);
        if (info.I1 is not null)
        {
            if (info.I1IsLapReasons)
            {
                w.WriteStartArray(info.I1);
                foreach (var code in LapReasonCodes.ToCodes((LapReasons)(ushort)r.I1)) w.WriteStringValue(code);
                w.WriteEndArray();
            }
            else w.WriteNumber(info.I1, r.I1);
        }
        WriteDouble(w, info.A, r.A);
        WriteDouble(w, info.B, r.B);
        WriteDouble(w, info.C, r.C);
        WriteDouble(w, info.D, r.D);
        w.WriteEndObject();
    }

    public static void Write(Utf8JsonWriter w, in DiagText t)
    {
        w.WriteStartObject();
        w.WriteString("t", Iso(t.WallTicksUtc));
        w.WriteNumber("seq", t.Seq);
        w.WriteString("lvl", LevelName(t.Level));
        w.WriteString("cat", t.Category);
        w.WriteString("msg", t.Message);
        w.WriteEndObject();
    }

    private static void WriteDouble(Utf8JsonWriter w, string? name, double v)
    {
        if (name is null) return;
        if (double.IsFinite(v)) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    private static string Iso(long ticks) =>
        new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string LevelName(DiagLevel l) => l switch
    {
        DiagLevel.Error => "error",
        DiagLevel.Warn => "warn",
        DiagLevel.Info => "info",
        DiagLevel.Debug => "debug",
        DiagLevel.Trace => "trace",
        _ => "off",
    };
}
