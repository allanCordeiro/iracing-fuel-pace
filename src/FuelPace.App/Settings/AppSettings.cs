using System.Text.Json;
using System.Text.Json.Serialization;
using FuelPace.Core.Strategy;

namespace FuelPace.App.Settings;

/// <summary>
/// camelCase string enums ("auto", "layered") for settings.json, read leniently: any case is accepted, and a value that is
/// not a name of <typeparamref name="T"/> (a typo, a number, another JSON type) reads as an undefined value that
/// <c>Normalize</c> replaces with the property's default. One bad value never makes the whole file corrupt (quarantined).
/// </summary>
public sealed class CamelCaseEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    /// <summary>Not a value of any enum here: <c>Normalize</c> turns it into the default.</summary>
    private static readonly T Undefined = (T)Enum.ToObject(typeof(T), -1);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            // Names only: Enum.TryParse would also take "1" or "-5".
            if (text is { Length: > 0 } && !char.IsAsciiDigit(text[0]) && text[0] != '-' && text[0] != '+'
                && Enum.TryParse<T>(text, ignoreCase: true, out var v) && Enum.IsDefined(v))
                return v;
            return Undefined;
        }
        reader.Skip(); // a number, a bool, an object or an array: ignored, whole
        return Undefined;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

[JsonConverter(typeof(CamelCaseEnumConverter<RenderBackend>))]
public enum RenderBackend { Auto, Composition, Layered }

// Settings records use set (not init): the System.Text.Json source generator leaves init-only properties absent from the file at default(T) instead of their initializer.
public sealed record OverlaySettings
{
    public const double MinScale = 0.5, MaxScale = 2.5, MinContentOpacity = 0.3;

    /// <summary>GDI device name of the monitor (e.g. <c>\\.\DISPLAY1</c>); null = primary.</summary>
    public string? MonitorDeviceName { get; set; }

    /// <summary>Physical pixels from the top-left corner of the monitor's work area.</summary>
    public int OffsetX { get; set; } = 40;
    public int OffsetY { get; set; } = 40;

    /// <summary>0.5–2.5.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>0–1.</summary>
    public double BackgroundOpacity { get; set; } = 0.6;

    /// <summary>0.3–1.</summary>
    public double ContentOpacity { get; set; } = 1.0;

    public bool ShowPaceLine { get; set; }
    public bool ShowInsightLine { get; set; } = true;
    public bool AutoHide { get; set; } = true;
    public RenderBackend RenderBackend { get; set; } = RenderBackend.Auto;

    internal OverlaySettings Normalize() => this with
    {
        Scale = Clamp(Scale, MinScale, MaxScale, 1.0),
        BackgroundOpacity = Clamp(BackgroundOpacity, 0, 1, 0.6),
        ContentOpacity = Clamp(ContentOpacity, MinContentOpacity, 1, 1.0),
        RenderBackend = Enum.IsDefined(RenderBackend) ? RenderBackend : RenderBackend.Auto,
    };

    internal static double Clamp(double v, double min, double max, double fallback)
        => double.IsNaN(v) ? fallback : Math.Min(max, Math.Max(min, v));
}

public sealed record HotkeySettings
{
    public const string DefaultToggleEdit = "Ctrl+Shift+F", DefaultToggleVisible = "Ctrl+Shift+H", DefaultDebugMark = "Ctrl+Shift+M";

    public string ToggleEdit { get; set; } = DefaultToggleEdit;
    public string ToggleVisible { get; set; } = DefaultToggleVisible;
    public string DebugMark { get; set; } = DefaultDebugMark;

    internal HotkeySettings Normalize() => new()
    {
        ToggleEdit = Valid(ToggleEdit, DefaultToggleEdit),
        ToggleVisible = Valid(ToggleVisible, DefaultToggleVisible),
        DebugMark = Valid(DebugMark, DefaultDebugMark),
    };

    static string Valid(string? text, string fallback) => Hotkey.TryParse(text, out var k) ? k.ToString() : fallback;
}

public sealed record DebugSettings
{
    public bool Enabled { get; set; }
    public bool RecordTelemetry { get; set; } = true;
    public double MaxRecordingsGB { get; set; } = 2;
}

public sealed record AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public OverlaySettings Overlay { get; set; } = new();

    [JsonConverter(typeof(StrategyConfigJsonConverter))]
    public StrategyConfig Strategy { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();
    public DebugSettings Debug { get; set; } = new();
    public bool StartWithWindows { get; set; }

    /// <summary>Clamps every range and replaces unparsable hotkeys with the defaults. Tolerates nulls from a hand-edited file.</summary>
    public AppSettings Normalize() => this with
    {
        Overlay = (Overlay ?? new()).Normalize(),
        Strategy = StrategyDto.From(Strategy ?? new()).ToConfig(),
        Hotkeys = (Hotkeys ?? new()).Normalize(),
        Debug = NormalizeDebug(Debug ?? new()),
    };

    static DebugSettings NormalizeDebug(DebugSettings d) => d with { MaxRecordingsGB = OverlaySettings.Clamp(d.MaxRecordingsGB, 0.1, 100, 2) };
}

/// <summary>Plain mirror of <see cref="StrategyConfig"/> whose setters do not validate, so an out-of-range value in a hand-edited file is clamped instead of throwing.</summary>
internal sealed class StrategyDto
{
    public double ReserveL { get; set; } = 0.5;
    public int MeanWindow { get; set; } = 5;
    public double MaxSavingFraction { get; set; } = 0.06;
    public double ExtraLaps { get; set; }
    [JsonConverter(typeof(CamelCaseEnumConverter<LapsMode>))]
    public LapsMode Mode { get; set; } = LapsMode.Auto;
    public int StintLaps { get; set; } = 25;
    public bool PracticeLapsFromTime { get; set; } = true;

    public static StrategyDto From(StrategyConfig c) => new()
    {
        ReserveL = c.ReserveL, MeanWindow = c.MeanWindow, MaxSavingFraction = c.MaxSavingFraction, ExtraLaps = c.ExtraLaps,
        Mode = c.Mode, StintLaps = c.StintLaps, PracticeLapsFromTime = c.PracticeLapsFromTime,
    };

    public StrategyConfig ToConfig() => new()
    {
        ReserveL = OverlaySettings.Clamp(ReserveL, 0, 20, 0.5),
        MeanWindow = Math.Clamp(MeanWindow, StrategyConfig.MinMeanWindow, StrategyConfig.MaxMeanWindow),
        MaxSavingFraction = OverlaySettings.Clamp(MaxSavingFraction, 0, 0.5, 0.06),
        ExtraLaps = OverlaySettings.Clamp(ExtraLaps, 0, 10, 0),
        Mode = Enum.IsDefined(Mode) ? Mode : LapsMode.Auto,
        StintLaps = Math.Clamp(StintLaps, 1, 500),
        PracticeLapsFromTime = PracticeLapsFromTime,
    };
}

internal sealed class StrategyConfigJsonConverter : JsonConverter<StrategyConfig>
{
    public override StrategyConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (JsonSerializer.Deserialize(ref reader, AppSettingsJsonContext.Default.StrategyDto) ?? new StrategyDto()).ToConfig();

    public override void Write(Utf8JsonWriter writer, StrategyConfig value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, StrategyDto.From(value), AppSettingsJsonContext.Default.StrategyDto);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = false, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(StrategyDto))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
