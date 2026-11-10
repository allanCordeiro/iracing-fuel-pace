using System.IO.MemoryMappedFiles;
using FuelPace.App.Live;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Telemetry;

namespace FuelPace.Agent.Live;

/// <summary>
/// The live iRacing source: the shared memory <c>Local\IRSDKMemMapFileName</c> mapped read-only once (pointer acquired once)
/// and the data-valid event <c>Local\IRSDKDataValidEvent</c>. Each <see cref="TryRead"/> waits up to 200 ms on the event and
/// reads the newest frame; the decisions (2 s without the status bit, layout change, 30 s stale, consistent session info) are
/// <see cref="LiveConnection"/>'s, and once it reported <see cref="ReadResult.Disconnected"/> the host disposes this source and
/// opens a new one.
/// <para>
/// Threading: <see cref="TryRead"/> on the telemetry thread, <see cref="ReadSessionInfoYaml"/> on the loop's session-info worker
/// at the same time. Every access to the view counts itself in a reference count; <see cref="Dispose"/> (any thread, once)
/// refuses new accesses and waits for the ones in flight before releasing the pointer, the view, the mapping and the event,
/// so a late read never touches unmapped memory (it returns <see cref="ReadResult.Disconnected"/> or throws
/// <see cref="ObjectDisposedException"/>).
/// </para>
/// </summary>
public sealed unsafe class IRacingMemoryReader : ITelemetrySource
{
    public const string DefaultMapName = @"Local\IRSDKMemMapFileName";
    public const string DefaultEventName = @"Local\IRSDKDataValidEvent";
    private const int WaitMs = 200;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _event;
    private readonly LiveConnection _conn;
    private readonly byte* _ptr;
    private readonly int _size;
    private int _users, _closing;

    private IRacingMemoryReader(MemoryMappedFile map, MemoryMappedViewAccessor view, EventWaitHandle ev, byte* ptr, int size,
        LiveConnection conn)
    {
        _map = map;
        _view = view;
        _event = ev;
        _ptr = ptr;
        _size = size;
        _conn = conn;
    }

    /// <summary>
    /// Opens the shared memory and the event. Null when either does not exist (the sim is not running) or the sim is not in a
    /// session yet: the host tries again every second.
    /// </summary>
    public static IRacingMemoryReader? TryOpen(string mapName = DefaultMapName, string eventName = DefaultEventName,
        DiagLog? log = null, TimeProvider? clock = null)
    {
        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        EventWaitHandle? ev = null;
        bool acquired = false;
        try
        {
            try { map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read); }
            catch (FileNotFoundException) { return null; }
            if (!EventWaitHandle.TryOpenExisting(eventName, out ev)) return null;
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            acquired = true;
            p += view.PointerOffset;
            int size = (int)Math.Min(view.Capacity, int.MaxValue);

            var conn = new LiveConnection(clock, log);
            if (!conn.TryConnect(new ReadOnlySpan<byte>(p, size))) return null;
            var reader = new IRacingMemoryReader(map, view, ev, p, size, conn);
            map = null; // owned by the reader now
            return reader;
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

    public VarLayout Layout => _conn.Layout;

    public int SessionInfoVersion => _conn.SessionInfoVersion;

    public ReadResult TryRead(ref TelemetryFrame frame)
    {
        if (!Enter()) return ReadResult.Disconnected;
        try
        {
            _event.WaitOne(WaitMs);
            return _conn.TryRead(new ReadOnlySpan<byte>(_ptr, _size), ref frame);
        }
        finally { Exit(); }
    }

    public string ReadSessionInfoYaml()
    {
        ObjectDisposedException.ThrowIf(!Enter(), this);
        try { return _conn.ReadSessionInfoYaml(new ReadOnlySpan<byte>(_ptr, _size)); }
        finally { Exit(); }
    }

    private bool Enter()
    {
        Interlocked.Increment(ref _users);
        if (Volatile.Read(ref _closing) == 0) return true;
        Interlocked.Decrement(ref _users);
        return false;
    }

    private void Exit() => Interlocked.Decrement(ref _users);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        var spin = new SpinWait();
        while (Volatile.Read(ref _users) != 0) spin.SpinOnce(); // at most one event wait (200 ms) or one YAML copy
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
        _event.Dispose();
    }
}
