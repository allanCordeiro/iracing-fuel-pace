using System.Diagnostics;

namespace FuelPace.Core.Telemetry;

/// <summary>Replays a recorded .ibt file as a telemetry source, optionally paced in real time.</summary>
public sealed class IbtTelemetrySource : ITelemetrySource
{
    private readonly IbtReader _reader;
    private readonly byte[] _row;
    private readonly double _speed;
    private int _index;
    private bool _paceStarted;
    private long _wall0;
    private double _sim0;

    public IbtTelemetrySource(string path, ReplaySpeed speed = ReplaySpeed.Max)
    {
        _reader = IbtReader.Open(path);
        _row = new byte[_reader.Header.BufLen];
        Layout = VarLayout.Resolve(_reader.Variables);
        _speed = speed switch { ReplaySpeed.X1 => 1.0, ReplaySpeed.X10 => 10.0, _ => 0.0 };
    }

    public VarLayout Layout { get; }
    public int SessionInfoVersion => 1;
    public string ReadSessionInfoYaml() => _reader.SessionInfoYaml;
    public int TickRate => _reader.Header.TickRate;
    public int RecordCount => _reader.RecordCount;

    public ReadResult TryRead(ref TelemetryFrame frame)
    {
        if (_index >= _reader.RecordCount) return ReadResult.EndOfStream;
        _reader.ReadRecord(_index++, _row);
        TelemetryFrameReader.Read(_row, Layout, ref frame);
        if (_speed > 0) Pace(frame.SessionTime);
        return ReadResult.Frame;
    }

    // Absolute deadline: t0_wall + (SessionTime - t0_sim) / speed. Sleep only when >= 2 ms remain, so a coarse
    // OS sleep granularity (15.6 ms on Windows) never accumulates drift. timeBeginPeriod is never called.
    private void Pace(double sessionTime)
    {
        if (!_paceStarted)
        {
            _paceStarted = true;
            _wall0 = Stopwatch.GetTimestamp();
            _sim0 = sessionTime;
            return;
        }
        double targetS = (sessionTime - _sim0) / _speed;
        double remainingMs = (targetS - Stopwatch.GetElapsedTime(_wall0).TotalSeconds) * 1000.0;
        if (remainingMs >= 2.0) Thread.Sleep((int)remainingMs);
    }

    public void Dispose() => _reader.Dispose();
}
