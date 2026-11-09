namespace FuelPace.Core.Telemetry;

public enum ReadResult { Frame, NoData, EndOfStream, Disconnected }

public interface ITelemetrySource : IDisposable
{
    /// <summary>Reads the next frame into <paramref name="frame"/> (only valid when <see cref="ReadResult.Frame"/>).</summary>
    ReadResult TryRead(ref TelemetryFrame frame);
    int SessionInfoVersion { get; }
    /// <summary>
    /// The current session-info YAML. May be called from a background thread concurrently with <see cref="TryRead"/> on the
    /// telemetry thread (the loop loads session info off-thread), so implementations must not share mutable state with it.
    /// </summary>
    string ReadSessionInfoYaml();
    VarLayout Layout { get; }
}
