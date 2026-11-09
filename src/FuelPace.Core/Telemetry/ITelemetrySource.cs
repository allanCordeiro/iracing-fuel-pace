namespace FuelPace.Core.Telemetry;

public enum ReadResult { Frame, NoData, EndOfStream, Disconnected }

public interface ITelemetrySource : IDisposable
{
    /// <summary>Reads the next frame into <paramref name="frame"/> (only valid when <see cref="ReadResult.Frame"/>).</summary>
    ReadResult TryRead(ref TelemetryFrame frame);
    int SessionInfoVersion { get; }
    string ReadSessionInfoYaml();
    VarLayout Layout { get; }
}
