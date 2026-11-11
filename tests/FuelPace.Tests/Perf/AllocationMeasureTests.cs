namespace FuelPace.Tests.Perf;

/// <summary>The measuring helper itself: a window that allocates nothing reads 0 even when a background GC ends inside it.</summary>
[Collection(PerfCollection.Name)]
public class AllocationMeasureTests
{
    [Fact]
    public void Background_gc_ending_inside_the_window_is_not_counted_as_allocation()
    {
        // Start a background gen2 GC, then leave a partly used allocation context on this thread. When the BGC's mark
        // phase ends it voids every thread's context without crediting the unused part back, which used to read as
        // up to ~8 KB "allocated" by a window that allocated nothing (CI: 4936 B in LapDetectorTests).
        var graph = new object[1_000_000]; // enough live objects that the background mark outlasts Start
        for (int i = 0; i < graph.Length; i++) graph[i] = new object();
        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
        GC.KeepAlive(new object());
        var window = AllocationMeasure.Start();
        Thread.Sleep(300); // allocation-free; gives the BGC time to finish its mark phase
        window.AssertNone();
        GC.KeepAlive(graph);
    }

    [Fact]
    public void Real_allocation_inside_the_window_is_still_counted()
    {
        var window = AllocationMeasure.Start();
        GC.KeepAlive(new byte[100]);
        Assert.True(window.Bytes >= 100, $"got {window.Bytes}");
    }
}
