using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FuelPace.Agent.Live;
using FuelPace.App.Live;
using FuelPace.Core.Telemetry;

namespace FuelPace.Windows.Tests;

/// <summary>The live reader over a real named mapping and event with private names, written by <see cref="IrsdkLiveImageWriter"/>.</summary>
public sealed class LiveReaderTests
{
    private const string Yaml1 = "---\nWeekendInfo:\n TrackName: zolder\n...\n";
    private const string Yaml2 = "---\nWeekendInfo:\n TrackName: spa\n...\n";

    private readonly string _map = @"Local\FuelPace.Tests.Map." + Guid.NewGuid().ToString("N");
    private readonly string _event = @"Local\FuelPace.Tests.Event." + Guid.NewGuid().ToString("N");

    /// <summary>Frame <paramref name="i"/>: every field the test checks carries <paramref name="i"/>, so a torn row shows.</summary>
    private static TelemetryFrame Frame(int i)
    {
        var f = new TelemetryFrame { SessionTime = i / 60.0, Lap = i, LapCompleted = i, FuelLevel = i, SessionNum = i };
        for (int c = 0; c < CarArray<int>.Length; c++)
        {
            f.CarIdxLapCompleted[c] = i;
            f.CarIdxLapDistPct[c] = i;
        }
        return f;
    }

    private static int Index(in TelemetryFrame f)
    {
        int i = f.Lap;
        Assert.True(f.LapCompleted == i && f.FuelLevel == i && f.SessionNum == i && f.SessionTime == i / 60.0, $"torn frame {i}");
        for (int c = 0; c < CarArray<int>.Length; c++)
            Assert.True(f.CarIdxLapCompleted[c] == i && f.CarIdxLapDistPct[c] == i, $"torn CarIdx in frame {i}");
        return i;
    }

    private IRacingMemoryReader Open()
    {
        var r = IRacingMemoryReader.TryOpen(_map, _event);
        Assert.NotNull(r);
        return r;
    }

    /// <summary>Reads until <paramref name="until"/> holds or 10 s pass; returns every frame index, checked for tearing.</summary>
    private static List<int> ReadFrames(IRacingMemoryReader r, Func<List<int>, bool> until)
    {
        var got = new List<int>();
        var f = new TelemetryFrame();
        var sw = Stopwatch.StartNew();
        while (!until(got) && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            var res = r.TryRead(ref f);
            Assert.NotEqual(ReadResult.Disconnected, res);
            if (res == ReadResult.Frame) got.Add(Index(f));
        }
        Assert.True(until(got), $"timed out with {got.Count} frames");
        return got;
    }

    [Fact]
    public void Frames_at_60_hz_arrive_in_order()
    {
        using var sim = new FakeSim(_map, _event, Yaml1);
        using var r = Open();
        Assert.Equal(0, r.Layout.MissingCount);
        using var feed = sim.Feed(Frame, count: 180, hz: 60);
        var got = ReadFrames(r, g => g.Count > 0 && g[^1] == 179);
        for (int k = 1; k < got.Count; k++) Assert.True(got[k] > got[k - 1], $"frame {got[k]} after {got[k - 1]}");
        Assert.True(got.Count >= 90, $"only {got.Count} of 180 frames read");
    }

    [Fact]
    public void A_writer_at_full_speed_never_yields_a_torn_frame()
    {
        using var sim = new FakeSim(_map, _event, Yaml1);
        using var r = Open();
        using var feed = sim.Feed(Frame, count: int.MaxValue, hz: 0);
        var got = ReadFrames(r, g => g.Count >= 2000); // Index() asserts every accepted row is whole
        for (int k = 1; k < got.Count; k++) Assert.True(got[k] > got[k - 1]);
    }

    [Fact]
    public void Leaving_the_session_for_more_than_two_seconds_is_disconnected()
    {
        using var sim = new FakeSim(_map, _event, Yaml1);
        using var r = Open();
        sim.Write(Frame(1));
        ReadFrames(r, g => g.Count == 1);

        sim.SetConnected(false);
        var sw = Stopwatch.StartNew();
        var f = new TelemetryFrame();
        ReadResult res;
        while ((res = r.TryRead(ref f)) == ReadResult.NoData && sw.Elapsed < TimeSpan.FromSeconds(10)) { }
        Assert.Equal(ReadResult.Disconnected, res);
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.8, 5.0);
        Assert.Equal(ReadResult.Disconnected, r.TryRead(ref f));
    }

    [Fact]
    public void Sim_closed_and_reopened_connects_again() // criterion 7
    {
        var sim = new FakeSim(_map, _event, Yaml1);
        var r = Open();
        sim.Write(Frame(1));
        ReadFrames(r, g => g.Count == 1);

        sim.SetConnected(false);
        sim.Dispose();      // the sim exits; the reader still maps the old memory
        var f = new TelemetryFrame();
        var sw = Stopwatch.StartNew();
        while (r.TryRead(ref f) != ReadResult.Disconnected) Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        r.Dispose();        // the host disposes the source on Disconnected
        Assert.Null(IRacingMemoryReader.TryOpen(_map, _event));

        using var again = new FakeSim(_map, _event, Yaml2);
        using var r2 = Open();
        again.Write(Frame(7));
        Assert.Equal([7], ReadFrames(r2, g => g.Count == 1));
        Assert.Equal(Yaml2, r2.ReadSessionInfoYaml());
    }

    [Fact]
    public void Nothing_to_open_without_the_sim_or_outside_a_session()
    {
        Assert.Null(IRacingMemoryReader.TryOpen(_map, _event));

        using (var mapOnly = MemoryMappedFile.CreateNew(_map, 4096))
            Assert.Null(IRacingMemoryReader.TryOpen(_map, _event)); // no event

        using var sim = new FakeSim(_map, _event, Yaml1);
        sim.SetConnected(false);
        Assert.Null(IRacingMemoryReader.TryOpen(_map, _event));
        sim.SetConnected(true);
        using var r = Open();
    }

    [Fact]
    public void Session_info_follows_the_update_counter()
    {
        using var sim = new FakeSim(_map, _event, Yaml1);
        using var r = Open();
        Assert.Equal(1, r.SessionInfoVersion);
        Assert.Equal(Yaml1, r.ReadSessionInfoYaml());

        sim.UpdateSessionInfo(Yaml2);
        sim.Write(Frame(1));
        ReadFrames(r, g => g.Count == 1);
        Assert.Equal(2, r.SessionInfoVersion);
        Assert.Equal(Yaml2, r.ReadSessionInfoYaml());
    }

    [Fact]
    public void Dispose_while_reads_are_in_flight_is_safe()
    {
        using var sim = new FakeSim(_map, _event, Yaml1);
        var r = Open();
        using var feed = sim.Feed(Frame, count: int.MaxValue, hz: 60);
        using var started = new CountdownEvent(2);
        Exception? frameError = null, yamlError = null;
        var frames = new Thread(() =>
        {
            var f = new TelemetryFrame();
            started.Signal();
            try { while (r.TryRead(ref f) != ReadResult.Disconnected) { } }
            catch (Exception e) { frameError = e; }
        });
        var yaml = new Thread(() =>
        {
            started.Signal();
            try
            {
                while (true) Assert.Equal(Yaml1, r.ReadSessionInfoYaml());
            }
            catch (ObjectDisposedException) { }
            catch (Exception e) { yamlError = e; }
        });
        frames.Start();
        yaml.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Thread.Sleep(300);

        r.Dispose(); // releases the view only once both readers are out of it
        Assert.True(frames.Join(TimeSpan.FromSeconds(5)) && yaml.Join(TimeSpan.FromSeconds(5)), "readers did not stop");
        Assert.Null(frameError);
        Assert.Null(yamlError);
        var after = new TelemetryFrame();
        Assert.Equal(ReadResult.Disconnected, r.TryRead(ref after));
        Assert.Throws<ObjectDisposedException>(() => r.ReadSessionInfoYaml());
        r.Dispose(); // twice is harmless
    }

    [Fact]
    public void Factory_names_come_from_the_environment()
    {
        string? oldMap = Environment.GetEnvironmentVariable(LiveSourceFactory.MapEnv);
        string? oldEvent = Environment.GetEnvironmentVariable(LiveSourceFactory.EventEnv);
        try
        {
            Environment.SetEnvironmentVariable(LiveSourceFactory.MapEnv, null);
            Environment.SetEnvironmentVariable(LiveSourceFactory.EventEnv, null);
            var defaults = new LiveSourceFactory(null, ReplaySpeed.X1);
            Assert.Equal(@"Local\IRSDKMemMapFileName", defaults.MapName);
            Assert.Equal(@"Local\IRSDKDataValidEvent", defaults.EventName);

            Environment.SetEnvironmentVariable(LiveSourceFactory.MapEnv, _map);
            Environment.SetEnvironmentVariable(LiveSourceFactory.EventEnv, _event);
            var factory = new LiveSourceFactory(null, ReplaySpeed.X1);
            Assert.Null(factory.TryOpen());
            using var sim = new FakeSim(_map, _event, Yaml1);
            using var src = factory.TryOpen();
            Assert.IsType<IRacingMemoryReader>(src);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveSourceFactory.MapEnv, oldMap);
            Environment.SetEnvironmentVariable(LiveSourceFactory.EventEnv, oldEvent);
        }
    }

    /// <summary>The sim's side: a writable named mapping, the data-valid event and the image writer.</summary>
    private sealed unsafe class FakeSim : IDisposable
    {
        private readonly IrsdkLiveImageWriter _w = new(TelemetryVars.All, 3, yamlCapacity: 64 * 1024);
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle _event;
        private readonly Lock _gate = new();
        private readonly byte* _ptr;

        public FakeSim(string map, string ev, string yaml)
        {
            _mmf = MemoryMappedFile.CreateNew(map, _w.TotalSize);
            _view = _mmf.CreateViewAccessor();
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _ptr = p + _view.PointerOffset;
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, ev);
            _w.Initialize(Image, yaml);
        }

        private Span<byte> Image => new(_ptr, _w.TotalSize);

        public void Write(in TelemetryFrame f)
        {
            lock (_gate) _w.WriteFrame(Image, f);
            _event.Set();
        }

        public void SetConnected(bool on)
        {
            lock (_gate) _w.SetConnected(Image, on);
        }

        public void UpdateSessionInfo(string yaml)
        {
            lock (_gate) _w.UpdateSessionInfo(Image, yaml);
        }

        /// <summary>Writes frames 0..count-1 on a thread, paced at <paramref name="hz"/> (0: as fast as possible).</summary>
        public IDisposable Feed(Func<int, TelemetryFrame> frame, int count, int hz) => new Feeder(this, frame, count, hz);

        public void Dispose()
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mmf.Dispose();
            _event.Dispose();
        }

        private sealed class Feeder : IDisposable
        {
            private readonly Thread _thread;
            private volatile bool _stop;

            public Feeder(FakeSim sim, Func<int, TelemetryFrame> frame, int count, int hz)
            {
                _thread = new Thread(() =>
                {
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < count && !_stop; i++)
                    {
                        sim.Write(frame(i));
                        if (hz <= 0) continue;
                        var due = TimeSpan.FromSeconds((i + 1) / (double)hz);
                        while (!_stop && sw.Elapsed < due) Thread.Sleep(1);
                    }
                }) { IsBackground = true, Name = "fake sim" };
                _thread.Start();
            }

            public void Dispose()
            {
                _stop = true;
                _thread.Join(TimeSpan.FromSeconds(5));
            }
        }
    }
}
