using System.Runtime.CompilerServices;

namespace FuelPace.Tests.Support;

/// <summary>
/// The only way a test measures allocations or GCs. A class that uses it must be in the <c>zero-allocation</c> collection
/// (<c>PerfCollection</c>), which <c>PerfCollectionGuardTests</c> enforces. A failure names the test and the byte count.
/// </summary>
/// <remarks>
/// <see cref="Start"/> runs a blocking GC so the window begins with an empty allocation context on this thread.
/// <c>GetAllocatedBytesForCurrentThread</c> is <c>alloc_bytes - (alloc_limit - alloc_ptr)</c>, and a background (gen2)
/// GC that ends its mark phase inside the window voids every thread's context (dotnet/runtime release/10.0 gc.cpp:
/// <c>repair_allocation_contexts(FALSE)</c> → <c>void_allocation</c>: ptr = limit = 0, alloc_bytes untouched). The
/// unused rest of the quantum (up to ~8 KB) then read as allocated by a window that allocated nothing: that was the
/// zero-allocation flake (7936 B locally, 4936 B on CI). No object was allocated; the jump equals the context's unused
/// rest. Such BGCs are started by the tests' own setup (LOH arrays) and their timing varies with the machine.
/// Every GC starts with <c>fix_allocation_contexts(TRUE)</c>, which retires the context with correct accounting
/// (ptr = limit = 0, unused part subtracted), and voiding an empty context is a no-op, so a BGC cannot move the
/// reading while the window stays allocation-free. Any real allocation in the window still counts.
/// </remarks>
public readonly struct AllocationMeasure
{
    private readonly long _bytes;
    private readonly int _gen0;
    private readonly string _test;

    private AllocationMeasure(string test)
    {
        _test = test;
        GC.Collect(); // see remarks: nothing may allocate on this thread between this and the reading below
        _gen0 = GC.CollectionCount(0);
        _bytes = GC.GetAllocatedBytesForCurrentThread();
    }

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
