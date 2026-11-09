namespace FuelPace.Tests.Perf;

/// <summary>
/// Every test that asserts an exact allocation or GC count lives in this collection. xUnit runs a collection with
/// <c>DisableParallelization</c> alone, after the parallel ones, so no other test thread is allocating, triggering gen0
/// collections or tiering methods while the window is measured.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerfCollection
{
    public const string Name = "zero-allocation";
}
