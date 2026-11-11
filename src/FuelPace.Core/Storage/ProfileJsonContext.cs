using System.Text.Json;
using System.Text.Json.Serialization;
using FuelPace.Core.Baselines;
using FuelPace.Core.Laps;

namespace FuelPace.Core.Storage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters = [typeof(NanSafeDoubleConverter), typeof(NanSafeSingleConverter), typeof(LapReasonsJsonConverter), typeof(ConfidenceJsonConverter)])]
[JsonSerializable(typeof(ProfileDocument))]
public sealed partial class ProfileJsonContext : JsonSerializerContext
{
}

/// <summary>NaN and infinities are written as <c>null</c>; <c>null</c> reads back as NaN (STJ would throw on both).</summary>
internal sealed class NanSafeDoubleConverter : JsonConverter<double>
{
    public override bool HandleNull => true;

    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}

internal sealed class NanSafeSingleConverter : JsonConverter<float>
{
    public override bool HandleNull => true;

    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? float.NaN : reader.GetSingle();

    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
    {
        if (float.IsFinite(value)) writer.WriteNumberValue(value);
        else writer.WriteNullValue();
    }
}

/// <summary><see cref="LapReasons"/> as an array of codes (<c>["incident","offtrack"]</c>).</summary>
internal sealed class LapReasonsJsonConverter : JsonConverter<LapReasons>
{
    public override LapReasons Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("reasons must be an array");
        var codes = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("reason codes must be strings");
            codes.Add(reader.GetString()!);
        }
        return LapReasonCodes.FromCodes(codes);
    }

    public override void Write(Utf8JsonWriter writer, LapReasons value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var c in LapReasonCodes.ToCodes(value)) writer.WriteStringValue(c);
        writer.WriteEndArray();
    }
}

/// <summary><see cref="Confidence"/> as <c>"baixa"</c> / <c>"media"</c> / <c>"alta"</c>.</summary>
internal sealed class ConfidenceJsonConverter : JsonConverter<Confidence>
{
    public override Confidence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "baixa" => Confidence.Baixa,
            "media" => Confidence.Media,
            "alta" => Confidence.Alta,
            var s => throw new JsonException($"unknown confidence '{s}'"),
        };

    public override void Write(Utf8JsonWriter writer, Confidence value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            Confidence.Baixa => "baixa",
            Confidence.Media => "media",
            _ => "alta",
        });
}
