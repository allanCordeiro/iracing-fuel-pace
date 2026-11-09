using System.Runtime.CompilerServices;

namespace FuelPace.Tests.Support;

/// <summary>
/// The only way a test measures allocations or GCs. A class that uses it must be in the <c>zero-allocation</c> collection
/// (<c>PerfCollection</c>), which <c>PerfCollectionGuardTests</c> enforces. A failure names the test and the byte count.
/// </summary>
public readonly struct AllocationMeasure
{
    private readonly long _bytes;
    private readonly int _gen0;
    private readonly string _test;

    private AllocationMeasure(string test) { _test = test; _gen0 = GC.CollectionCount(0); _bytes = GC.GetAllocatedBytesForCurrentThread(); }

    public static AllocationMeasure Start([CallerMemberName] string test = "") => new(test);

    /// <summary>Bytes allocated by this thread since <see cref="Start"/>.</summary>
    public long Bytes => GC.GetAllocatedBytesForCurrentThread() - _bytes;

    /// <summary>Gen0 collections (all threads) since <see cref="Start"/>.</summary>
    public int Gen0 => GC.CollectionCount(0) - _gen0;

    public void AssertNone()
    {
        long bytes = Bytes;
        Assert.True(bytes == 0, $"{_test}: expected 0 bytes allocated on the measured thread, got {bytes} (gen0 collections meanwhile: {Gen0})");
    }
}
