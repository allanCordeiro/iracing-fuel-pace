using FuelPace.Core.Telemetry;

namespace FuelPace.Tests.Support;

/// <summary>A live-like <see cref="ITelemetrySource"/> driven by the test: results come from <see cref="Script"/>, then
/// <see cref="WhenEmpty"/>; every <see cref="ReadResult.Frame"/> copies <see cref="Frame"/>. Allocation-free per read.</summary>
public sealed class ScriptedTelemetrySource : ITelemetrySource
{
    public readonly Queue<ReadResult> Script = new();
    public ReadResult WhenEmpty = ReadResult.NoData;
    public TelemetryFrame Frame;
    public VarLayout Layout { get; set; } = VarLayout.Resolve([]);
    public int SessionInfoVersion { get; set; } = 1;
    public string Yaml = SampleYaml.Zolder;
    /// <summary>Runs on whatever thread reads the session info (the loop's worker in background mode).</summary>
    public Func<string>? YamlHook;

    public ReadResult TryRead(ref TelemetryFrame frame)
    {
        var r = Script.Count > 0 ? Script.Dequeue() : WhenEmpty;
        if (r == ReadResult.Frame) frame = Frame;
        return r;
    }

    public string ReadSessionInfoYaml() => YamlHook?.Invoke() ?? Yaml;
    public void Dispose() { }
}
