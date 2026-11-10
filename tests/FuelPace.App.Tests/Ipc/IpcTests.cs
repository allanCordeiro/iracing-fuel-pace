using System.IO.Pipes;
using System.Text;
using FuelPace.App.Diagnostics;
using FuelPace.App.Ipc;
using FuelPace.App.Settings;
using FuelPace.Core.Capture;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;

namespace FuelPace.App.Tests.Ipc;

public sealed class IpcTests
{
    static string NewName() => "fp" + Guid.NewGuid().ToString("N")[..10];
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    static AgentSnapshot Snap(string version = "0.1.0") => new(
        version, true, "car__track", "Ligier JS P320", "Zolder Alt", "Race", true,
        [new CapturedLapDto(3, 92.123, 2.5, 71.5, ["pit"], false), new CapturedLapDto(4, null, double.NaN, 50, [], true)],
        1, 2, new AppSettings(), true, "layered", false, ["Ctrl+Shift+F"], ["\\\\.\\DISPLAY1"]);

    static async Task<T> WaitFor<T>(Func<T?> probe) where T : class
    {
        var end = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < end) { var v = probe(); if (v != null) return v; await Task.Delay(10); }
        throw new TimeoutException();
    }

    static async Task WaitUntil(Func<bool> cond)
    {
        var end = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < end) { if (cond()) return; await Task.Delay(10); }
        throw new TimeoutException();
    }

    static IpcServer Server(string name, Func<IpcMessage, IpcServerContext, ValueTask> h, DiagLog? log = null, IpcServerOptions? options = null)
    {
        var s = new IpcServer(name, h, log ?? DiagLog.Null, options);
        s.Start();
        return s;
    }

    static async Task<IpcClient> Connect(string name)
    {
        var c = new IpcClient(name);
        Assert.True(await c.ConnectAsync(Timeout));
        return c;
    }

    [Fact]
    public async Task Request_and_reply_round_trip()
    {
        var name = NewName();
        using var server = Server(name, async (m, ctx) =>
        {
            if (m is GetSnapshot) await ctx.ReplyAsync(new Snapshot(Snap()));
        });
        await using var client = await Connect(name);
        IpcMessage? got = null;
        client.Received += m => got = m;
        await client.SendAsync(new GetSnapshot());
        var reply = Assert.IsType<Snapshot>(await WaitFor(() => got));
        var expected = Snap().Settings;
        Assert.Equal("0.1.0", reply.S.Version);
        Assert.Equal("Zolder Alt", reply.S.TrackName);
        Assert.Equal(2, reply.S.CaptureLaps.Count);
        Assert.Equal(92.123, reply.S.CaptureLaps[0].TimeS);
        Assert.Equal(["pit"], reply.S.CaptureLaps[0].Reasons);
        Assert.Null(reply.S.CaptureLaps[1].TimeS);
        Assert.True(double.IsNaN(reply.S.CaptureLaps[1].FuelL));
        Assert.Equal(expected.Overlay.Scale, reply.S.Settings.Overlay.Scale);
        Assert.Equal(["Ctrl+Shift+F"], reply.S.HotkeyFailures);
    }

    [Fact]
    public async Task Broadcast_reaches_all_clients()
    {
        var name = NewName();
        using var server = Server(name, (_, _) => ValueTask.CompletedTask);
        var clients = new List<IpcClient>();
        var got = new List<IpcMessage?>();
        for (int i = 0; i < 3; i++)
        {
            var c = await Connect(name);
            int idx = i; got.Add(null);
            c.Received += m => got[idx] = m;
            clients.Add(c);
        }
        await WaitUntil(() => server.ClientCount == 3);
        await server.BroadcastAsync(new Notice("info", "olá"));
        await WaitUntil(() => got.All(g => g != null));
        Assert.All(got, g => Assert.Equal(new Notice("info", "olá"), g));
        foreach (var c in clients) await c.DisposeAsync();
    }

    [Fact]
    public async Task Client_disconnect_does_not_break_server()
    {
        var name = NewName();
        using var server = Server(name, async (m, ctx) => { if (m is GetSnapshot) await ctx.ReplyAsync(new Error("x")); });
        var first = await Connect(name);
        await WaitUntil(() => server.ClientCount == 1);
        await first.DisposeAsync();
        await WaitUntil(() => server.ClientCount == 0);
        await server.BroadcastAsync(new Notice("info", "nobody"));
        await using var second = await Connect(name);
        IpcMessage? got = null;
        second.Received += m => got = m;
        await second.SendAsync(new GetSnapshot());
        Assert.IsType<Error>(await WaitFor(() => got));
    }

    [Fact]
    public async Task Oversized_message_disconnects_client()
    {
        var name = NewName();
        int handled = 0;
        using var server = Server(name, (_, _) => { Interlocked.Increment(ref handled); return ValueTask.CompletedTask; });
        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await raw.ConnectAsync((int)Timeout.TotalMilliseconds, TestContext.Current.CancellationToken);
        await WaitUntil(() => server.ClientCount == 1);
        var junk = new byte[1024 * 1024];
        Array.Fill(junk, (byte)'a');
        try { for (int i = 0; i < 10; i++) await raw.WriteAsync(junk, TestContext.Current.CancellationToken); } catch (IOException) { }
        await WaitUntil(() => server.ClientCount == 0);
        Assert.Equal(0, handled);
        // server still accepts new clients
        await using var ok = await Connect(name);
    }

    [Fact]
    public async Task Unknown_type_is_ignored_and_logged()
    {
        var name = NewName();
        var log = new DiagLog(DiagLevel.Debug);
        int handled = 0;
        IpcMessage? last = null;
        using var server = Server(name, (m, _) => { Interlocked.Increment(ref handled); last = m; return ValueTask.CompletedTask; }, log);
        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await raw.ConnectAsync((int)Timeout.TotalMilliseconds, TestContext.Current.CancellationToken);
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"bogus\",\"x\":1}\nnot json at all\n{\"type\":\"setCapture\",\"on\":true}\n");
        await raw.WriteAsync(bytes, TestContext.Current.CancellationToken);
        await raw.FlushAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new SetCapture(true), await WaitFor(() => last));
        Assert.Equal(1, handled);
        Assert.Equal(1, server.ClientCount);
        var texts = new List<DiagText>();
        while (log.TryDequeueText(out var t)) texts.Add(t);
        Assert.Contains(texts, t => t.Category == "ipc");
        Assert.True(texts.Count >= 2);
    }

    [Fact]
    public async Task Handler_exception_does_not_kill_connection()
    {
        var name = NewName();
        using var server = Server(name, (m, ctx) => m is SetCapture ? throw new InvalidOperationException("boom") : ctx.ReplyAsync(new Error("pong")));
        await using var client = await Connect(name);
        IpcMessage? got = null;
        client.Received += m => got = m;
        await client.SendAsync(new SetCapture(true));
        await client.SendAsync(new GetSnapshot());
        Assert.Equal(new Error("pong"), await WaitFor(() => got));
    }

    [Fact]
    public async Task Slow_client_is_dropped_without_blocking_broadcast()
    {
        var name = NewName();
        using var server = Server(name, (_, _) => ValueTask.CompletedTask);
        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await raw.ConnectAsync((int)Timeout.TotalMilliseconds, TestContext.Current.CancellationToken); // never reads
        await WaitUntil(() => server.ClientCount == 1);
        var big = new Error(new string('x', 64 * 1024));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++) await server.BroadcastAsync(big);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"broadcast blocked: {sw.Elapsed}");
        await WaitUntil(() => server.ClientCount == 0);
    }

    [Fact]
    public async Task Client_count_is_capped_at_four_and_fifth_is_served_after_a_slot_frees()
    {
        var name = NewName();
        using var server = Server(name, (_, _) => ValueTask.CompletedTask);
        var clients = new List<IpcClient>();
        for (int i = 0; i < 4; i++) clients.Add(await Connect(name));
        await WaitUntil(() => server.ClientCount == 4);
        var fifth = new IpcClient(name);
        IpcMessage? got = null;
        fifth.Received += m => got = m;
        _ = await fifth.ConnectAsync(TimeSpan.FromMilliseconds(300));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(4, server.ClientCount);
        await clients[0].DisposeAsync();
        // On Windows a timed-out connect must be retried; on Unix the queued connection is accepted.
        if (!await fifth.ConnectAsync(Timeout)) { fifth = new IpcClient(name); fifth.Received += m => got = m; Assert.True(await fifth.ConnectAsync(Timeout)); }
        await WaitUntil(() => server.ClientCount == 4);
        await server.BroadcastAsync(new Notice("info", "hi"));
        Assert.Equal(new Notice("info", "hi"), await WaitFor(() => got));
        await fifth.DisposeAsync();
        for (int i = 1; i < 4; i++) await clients[i].DisposeAsync();
    }

    static readonly IpcServerOptions Fast = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task Client_that_never_sends_is_dropped_and_slot_reused()
    {
        var name = NewName();
        using var server = Server(name, (_, _) => ValueTask.CompletedTask, options: Fast);
        var idle = new List<NamedPipeClientStream>();
        for (int i = 0; i < 4; i++)
        {
            var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await raw.ConnectAsync((int)Timeout.TotalMilliseconds, TestContext.Current.CancellationToken);
            idle.Add(raw);
        }
        await WaitUntil(() => server.ClientCount == 4);
        await WaitUntil(() => server.ClientCount == 0);
        await using var ok = await Connect(name);
        await WaitUntil(() => server.ClientCount == 1);
        foreach (var r in idle) await r.DisposeAsync();
    }

    [Fact]
    public async Task Client_with_partial_line_is_dropped()
    {
        var name = NewName();
        // long handshake, short partial deadline: only the partial rule can fire
        using var server = Server(name, (_, _) => ValueTask.CompletedTask, options: new(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(300)));
        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await raw.ConnectAsync((int)Timeout.TotalMilliseconds, TestContext.Current.CancellationToken);
        await raw.WriteAsync("{\"type\":\"hel"u8.ToArray(), TestContext.Current.CancellationToken);
        await raw.FlushAsync(TestContext.Current.CancellationToken);
        await WaitUntil(() => server.ClientCount == 1);
        await WaitUntil(() => server.ClientCount == 0);
    }

    [Fact]
    public async Task Client_that_said_hello_and_idles_is_not_dropped()
    {
        var name = NewName();
        using var server = Server(name, (_, _) => ValueTask.CompletedTask, options: Fast);
        await using var client = await Connect(name);
        IpcMessage? got = null;
        client.Received += m => got = m;
        await client.SendAsync(new Hello("0.1.0"));
        await Task.Delay(1200, TestContext.Current.CancellationToken);
        Assert.Equal(1, server.ClientCount);
        await server.BroadcastAsync(new Notice("info", "still here"));
        Assert.Equal(new Notice("info", "still here"), await WaitFor(() => got));
    }

    [Fact]
    public async Task Handler_cancellation_does_not_drop_client()
    {
        var name = NewName();
        using var server = Server(name, (m, ctx) => m is SetCapture ? throw new OperationCanceledException() : ctx.ReplyAsync(new Error("pong")));
        await using var client = await Connect(name);
        IpcMessage? got = null;
        client.Received += m => got = m;
        await client.SendAsync(new SetCapture(true));
        await client.SendAsync(new GetSnapshot());
        Assert.Equal(new Error("pong"), await WaitFor(() => got));
    }

    [Fact]
    public async Task User_dispose_does_not_raise_Disconnected()
    {
        var name = NewName();
        using var server = Server(name, (_, _) => ValueTask.CompletedTask);
        var client = await Connect(name);
        bool disc = false;
        client.Disconnected += () => disc = true;
        await client.DisposeAsync();
        await WaitUntil(() => server.ClientCount == 0);
        Assert.False(disc);
    }

    [Fact]
    public async Task Client_reports_disconnect_when_server_goes_away()
    {
        var name = NewName();
        var server = Server(name, (_, _) => ValueTask.CompletedTask);
        await using var client = await Connect(name);
        bool disc = false;
        client.Disconnected += () => disc = true;
        server.Dispose();
        await WaitUntil(() => disc);
    }

    [Fact]
    public async Task Connect_times_out_without_server()
    {
        await using var c = new IpcClient(NewName());
        Assert.False(await c.ConnectAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Captured_lap_dto_maps_reasons_and_nan()
    {
        var dto = CapturedLapDto.From(new CapturedLap { Lap = 7, TimeS = null, FuelL = 2.5, FullThrottlePct = 60, Reasons = LapReasons.Pit | LapReasons.Incident });
        Assert.Equal(7, dto.Lap);
        Assert.Null(dto.TimeS);
        Assert.Equal(["pit", "incident"], dto.Reasons);
        Assert.False(dto.Valid);
        Assert.True(CapturedLapDto.From(new CapturedLap { Lap = 1 }).Valid);
    }

    public static IEnumerable<object[]> AllMessages()
    {
        var lap = new CapturedLapDto(1, 90.5, 2.1, 70, ["offtrack"], false);
        IpcMessage[] all =
        [
            new Hello("0.1.0"), new GetSnapshot(), new SetCapture(true), new UpdateSettings(new AppSettings()), new SetEditMode(false),
            new ListProfiles(), new GetProfile("a__b"), new ProfileCommand("a__b", "setLapExcluded", "cap1", 4, true),
            new ProfileCommand("a__b", "deleteCapture", "cap1", null, null), new CreateBundle(true), new SetDebugMode(true), new ShowSettingsRequest(),
            new Snapshot(Snap()), new CaptureLapAdded(lap, 1, 2), new CaptureFinished("a__b", "cap1"),
            new ProfileList([new ProfileSummary("a__b", "Car", "Track", 3, 40, "Alta"), new ProfileSummary("c__d", "Car2", "Track2", 0, 0, null)]),
            new Profile("a__b", "{\"x\":1}"), new SettingsApplied(new AppSettings()), new BundleCreated("C:\\x.zip"), new Notice("warn", "t"), new Error("e"),
        ];
        return all.Select(m => new object[] { m });
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void All_message_types_serialize_round_trip(IpcMessage m)
    {
        var bytes = IpcJson.Serialize(m);
        Assert.DoesNotContain((byte)'\n', bytes);
        Assert.True(IpcJson.TryDeserialize(bytes, out var back, out _));
        Assert.Equal(m.GetType(), back!.GetType());
        Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(IpcJson.Serialize(back)));
        Assert.StartsWith("{\"type\":\"", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Every_derived_message_is_covered_by_the_theory()
    {
        var covered = AllMessages().Select(a => a[0].GetType()).ToHashSet();
        var all = typeof(IpcMessage).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(IpcMessage)));
        Assert.All(all, t => Assert.Contains(t, covered));
    }
}
