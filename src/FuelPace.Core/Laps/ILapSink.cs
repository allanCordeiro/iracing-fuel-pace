namespace FuelPace.Core.Laps;

public interface ILapSink
{
    void OnLapCompleted(in CompletedLap lap);
}
