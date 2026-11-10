using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using FuelPace.App.Live;
using FuelPace.Core.Telemetry;
using Microsoft.Win32.SafeHandles;

namespace FuelPace.FakeSim;

/// <summary>
/// The sim's side of the irsdk protocol, as far as the Agent's reader cares: a named page-file mapping holding the image
/// <see cref="IrsdkLiveImageWriter"/> lays out (3 rotating buffers, rising <c>tickCount</c> published after the row) and the
/// named data-valid event.
/// <list type="bullet">
/// <item>The mapping is created or opened, as the sim's <c>CreateFileMapping</c> does: a reader still holding the previous
/// run's mapping (it has not noticed the sim left yet) sees the same memory come back.</item>
/// <item>The event is manual-reset and pulsed once per frame (<c>PulseEvent</c>): every client waiting at that moment wakes
/// (the Agent next to SimHub, say) and none is left signaled. An auto-reset event would wake only one of several readers.
/// The Agent's reader does not depend on the mode: it waits at most 200 ms and then reads the newest buffer, so a pulse it
/// misses while busy costs it at most one frame.</item>
/// <item><see cref="Dispose"/> clears the status bit, pulses once more (a waiting reader sees it right away) and closes both
/// handles; the memory lives on while a reader still maps it, with the bit off (its 2 s grace, then disconnected). A killed
/// FakeSim leaves the bit on, like a crashed sim: the reader gives up after 30 s without a new tick.</item>
/// </list>
/// One thread at a time (the publishing loop).
/// </summary>
internal sealed unsafe partial class SharedMemorySim : IDisposable
{
    private readonly IrsdkLiveImageWriter _writer;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _event;
    private readonly byte* _ptr;
    private string _yaml;
    private bool _disposed;

    private SharedMemorySim(IrsdkLiveImageWriter writer, MemoryMappedFile map, MemoryMappedViewAccessor view, EventWaitHandle ev,
        byte* ptr, string yaml)
    {
        _writer = writer;
        _map = map;
        _view = view;
        _event = ev;
        _ptr = ptr;
        _yaml = yaml;
    }

    /// <summary>Creates (or opens) the mapping and the event and writes the headers and <paramref name="yaml"/>: the image is
    /// in a session (status bit on), with no frame yet.</summary>
    public static SharedMemorySim Create(string mapName, string eventName, IReadOnlyList<VarSpec> vars, string yaml)
    {
        var writer = new IrsdkLiveImageWriter(vars, numBuf: 3);
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        EventWaitHandle? ev = null;
        bool acquired = false;
        try
        {
            map = MemoryMappedFile.CreateOrOpen(mapName, writer.TotalSize, MemoryMappedFileAccess.ReadWrite);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            if (view.Capacity < writer.TotalSize)
                throw new IOException($"a mapping named {mapName} already exists with {view.Capacity} bytes; this layout needs "
                    + $"{writer.TotalSize} (is another sim running?)");
            ev = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            acquired = true;
            var sim = new SharedMemorySim(writer, map, view, ev, p + view.PointerOffset, yaml);
            writer.Initialize(sim.Image, yaml);
            map = null; // owned by the sim now
            return sim;
        }
        finally
        {
            if (map is not null)
            {
                if (acquired) view!.SafeMemoryMappedViewHandle.ReleasePointer();
                view?.Dispose();
                ev?.Dispose();
                map.Dispose();
            }
        }
    }

    /// <summary>Bytes of the image (the mapping may be larger: page-rounded, or left by a bigger layout).</summary>
    public int Size => _writer.TotalSize;

    private Span<byte> Image => new(_ptr, _writer.TotalSize);

    /// <summary>Writes <paramref name="f"/> into the next buffer, publishes its tick and wakes the readers.</summary>
    public void Publish(in TelemetryFrame f)
    {
        _writer.WriteFrame(Image, in f);
        Pulse();
    }

    /// <summary>Rewrites the session info and increments <c>sessionInfoUpdate</c>, when the text changed.</summary>
    public bool UpdateSessionInfo(string yaml)
    {
        if (string.Equals(yaml, _yaml, StringComparison.Ordinal)) return false;
        _writer.UpdateSessionInfo(Image, yaml);
        _yaml = yaml;
        return true;
    }

    private void Pulse()
    {
        if (!PulseEvent(_event.SafeWaitHandle)) throw new IOException("PulseEvent failed: " + Marshal.GetLastPInvokeErrorMessage());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.SetConnected(Image, false);
        PulseEvent(_event.SafeWaitHandle); // best effort: the handles close either way
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
        _event.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PulseEvent(SafeWaitHandle handle);
}
