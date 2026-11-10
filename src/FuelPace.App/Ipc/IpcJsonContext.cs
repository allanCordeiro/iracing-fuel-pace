using System.Text.Json;
using System.Text.Json.Serialization;

namespace FuelPace.App.Ipc;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false,
    Converters = [typeof(IpcNanSafeDoubleConverter)])]
[JsonSerializable(typeof(IpcMessage))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;

/// <summary>NaN/infinity travel as <c>null</c> (STJ would throw); <c>null</c> reads back as NaN.</summary>
internal sealed class IpcNanSafeDoubleConverter : JsonConverter<double>
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

internal static class IpcJson
{
    /// <summary>One line of UTF-8 JSON, without the trailing newline.</summary>
    public static byte[] Serialize(IpcMessage m) => JsonSerializer.SerializeToUtf8Bytes(m, IpcJsonContext.Default.IpcMessage);

    /// <summary>Never throws: malformed JSON or an unknown <c>type</c> yields false with the reason.</summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> line, out IpcMessage? message, out string error)
    {
        try
        {
            message = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcMessage);
            error = "";
            if (message != null) return true;
            error = "null message";
            return false;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            message = null;
            error = ex.Message;
            return false;
        }
    }
}
