using System.Buffers.Binary;
using FuelPace.App.Live;
using FuelPace.App.Simulation;
using FuelPace.App.Tests.Fakes;
using FuelPace.Core.Telemetry;
using static FuelPace.App.Tests.Recording.RecordingTestSupport;

namespace FuelPace.App.Tests.Live;

public class LiveConnectionTests
{
    private const string Yaml1 = "---\nWeekendInfo:\n TrackName: zolder\n...\n";
    private const string Yaml2 = "---\nWeekendInfo:\n TrackName: spa\n...\n";

    private readonly ManualClock _clock = new(SyntheticRace.T0);
    private readonly IrsdkLiveImageWriter _w = new(TelemetryVars.All, 3, yamlCapacity: 4096);
    private readonly byte[] _image;
    private readonly List<TelemetryFrame> _frames = SyntheticRace.Frames(1, withCarIdx: true).Take(20).ToList();
    private int _next;

    public LiveConnectionTests()
    {
        _image = new byte[_w.TotalSize];
        _w.Initialize(_image, Yaml1);
    }

    private LiveConnection Connected()
    {
        var c = new LiveConnection(_clock);
        Assert.True(c.TryConnect(_image));
        return c;
    }

    private TelemetryFrame Write()
    {
        var f = _frames[_next++];
        _w.WriteFrame(_image, f);
        return f;
    }

    private ReadResult Read(LiveConnection c) => Read(c, out _);

    private ReadResult Read(LiveConnection c, out TelemetryFrame f)
    {
        f = new TelemetryFrame();
        return c.TryRead(_image, ref f);
    }

    [Fact]
    public void Connects_only_to_a_connected_image()
    {
        var c = new LiveConnection(_clock);
        Assert.False(c.TryConnect([]));
        _w.SetConnected(_image, false);
        Assert.False(c.TryConnect(_image));
        _w.SetConnected(_image, true);
        Assert.True(c.TryConnect(_image));
        Assert.Equal(0, c.Layout.MissingCount);
    }

    [Fact]
    public void Frames_flow_and_no_new_data_is_no_data()
    {
        var c = Connected();
        Assert.Equal(ReadResult.NoData, Read(c));
        var e = Write();
        Assert.Equal(ReadResult.Frame, Read(c, out var f));
        AssertSameFrame(e, f, 0);
        Assert.Equal(ReadResult.NoData, Read(c));
        Assert.Equal(1, c.SessionInfoVersion);
    }

    [Fact]
    public void A_short_disconnection_keeps_the_connection()
    {
        var c = Connected();
        Write();
        Assert.Equal(ReadResult.Frame, Read(c));

        _w.SetConnected(_image, false);
        Assert.Equal(ReadResult.NoData, Read(c));
        _clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(ReadResult.NoData, Read(c));

        _w.SetConnected(_image, true);
        var e = Write();
        Assert.Equal(ReadResult.NoData, Read(c)); // the same layout is resolved again
        Assert.Equal(ReadResult.Frame, Read(c, out var f));
        AssertSameFrame(e, f, 1);
    }

    [Fact]
    public void Not_connected_for_more_than_two_seconds_is_disconnected_for_good()
    {
        var c = Connected();
        Write();
        Assert.Equal(ReadResult.Frame, Read(c));

        _w.SetConnected(_image, false);
        Assert.Equal(ReadResult.NoData, Read(c));
        _clock.Advance(TimeSpan.FromSeconds(2.1));
        Assert.Equal(ReadResult.Disconnected, Read(c));

        // The host reopens the source; this connection stays ended even if the sim comes back.
        _w.SetConnected(_image, true);
        Write();
        Assert.Equal(ReadResult.Disconnected, Read(c));
        Assert.Equal(ReadResult.Disconnected, Read(c));
    }

    [Fact]
    public void A_layout_change_is_a_disconnection()
    {
        var c = Connected();
        Write();
        Assert.Equal(ReadResult.Frame, Read(c));

        var other = new IrsdkLiveImageWriter([new("SessionTime", VarType.Double), new("Lap", VarType.Int)], 3, 4096);
        Assert.True(other.TotalSize <= _image.Length);
        other.Initialize(_image, Yaml1);
        other.WriteFrame(_image, _frames[5]);
        Assert.Equal(ReadResult.Disconnected, Read(c));
        Assert.Equal(ReadResult.Disconnected, Read(c));
    }

    [Fact]
    public void Connected_but_stale_for_thirty_seconds_is_disconnected()
    {
        // A sim that died without clearing the status bit (the irsdk client uses the same 30 s timeout).
        var c = Connected();
        Write();
        Assert.Equal(ReadResult.Frame, Read(c));
        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(ReadResult.NoData, Read(c));
        Write();
        Assert.Equal(ReadResult.Frame, Read(c));
        _clock.Advance(TimeSpan.FromSeconds(30.5));
        Assert.Equal(ReadResult.Disconnected, Read(c));
    }

    [Fact]
    public void Stale_timer_starts_at_connection_when_no_frame_ever_arrives()
    {
        var c = Connected();
        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(ReadResult.NoData, Read(c));
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(ReadResult.Disconnected, Read(c));
    }

    [Fact]
    public void Session_info_version_follows_the_header()
    {
        var c = Connected();
        Write();
        Read(c);
        Assert.Equal(1, c.SessionInfoVersion);
        _w.UpdateSessionInfo(_image, Yaml2);
        Write();
        Read(c);
        Assert.Equal(2, c.SessionInfoVersion);
        Assert.Equal(Yaml2, c.ReadSessionInfoYaml(_image));
    }

    [Fact]
    public void Yaml_rewritten_while_read_is_read_again()
    {
        var c = Connected();
        int calls = 0;
        c.BetweenYamlReadsForTests = () =>
        {
            if (calls++ == 0) _w.UpdateSessionInfo(_image, Yaml2); // the sim rewrites it during our first copy
        };
        Assert.Equal(Yaml2, c.ReadSessionInfoYaml(_image));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Yaml_that_keeps_changing_is_skipped()
    {
        var c = Connected();
        int calls = 0;
        c.BetweenYamlReadsForTests = () =>
        {
            calls++;
            int v = BinaryPrimitives.ReadInt32LittleEndian(_image.AsSpan(12));
            BinaryPrimitives.WriteInt32LittleEndian(_image.AsSpan(12), v + 1);
        };
        var e = Assert.Throws<FormatException>(() => c.ReadSessionInfoYaml(_image));
        Assert.Contains("changed while", e.Message);
        Assert.Equal(LiveConnection.YamlAttempts, calls);
    }

    [Fact]
    public void Yaml_of_too_short_memory_is_empty()
    {
        var c = Connected();
        Assert.Equal("", c.ReadSessionInfoYaml(new byte[10]));
    }
}
