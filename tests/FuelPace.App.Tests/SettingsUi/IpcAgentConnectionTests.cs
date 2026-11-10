using FuelPace.App.Ipc;
using FuelPace.App.SettingsUi;
using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Tests.SettingsUi;

/// <summary><see cref="IpcAgentConnection"/> over a real named pipe, against an in-process <see cref="IpcServer"/>.</summary>
public sealed class IpcAgentConnectionTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    static string NewName() => "fpc" + Guid.NewGuid().ToString("N")[..10];

    static IpcServer Server(string name, IpcServerOptions? options = null)
    {
        var s = new IpcServer(name, async (m, ctx) =>
        {
            if (m is Hello) await ctx.ReplyAsync(new Snapshot(SettingsRig.Snap()));
        }, DiagLog.Null, options);
        s.Start();
        return s;
    }

    static async Task WaitUntil(Func<bool> cond)
    {
        var end = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < end) { if (cond()) return; await Task.Delay(10, TestContext.Current.CancellationToken); }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Hello_is_answered_with_a_snapshot()
    {
        var name = NewName();
        using var server = Server(name);
        await using var client = new IpcClient(name);
        var conn = new IpcAgentConnection(client, () => { }, Wait);
        var got = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Received += m => got.TrySetResult(m);

        Assert.True(await conn.ConnectAsync());
        Assert.True(conn.IsConnected);
        await conn.SendAsync(new Hello("0.1.0+test"));

        var snap = Assert.IsType<Snapshot>(await got.Task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal("Zolder Circuit (Alternate)", snap.S.TrackName);
        Assert.Equal(9, snap.S.CaptureLaps.Count);
    }

    [Fact]
    public async Task No_Agent_means_not_connected_after_the_timeout()
    {
        await using var client = new IpcClient(NewName());
        var conn = new IpcAgentConnection(client, () => { }, TimeSpan.FromMilliseconds(200));

        Assert.False(await conn.ConnectAsync().WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.False(conn.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await conn.SendAsync(new Hello("x")));
    }

    [Fact]
    public async Task The_Agent_going_away_raises_Disconnected_and_a_restarted_Agent_is_reached_again()
    {
        var name = NewName();
        var server = Server(name);
        await using var client = new IpcClient(name);
        var conn = new IpcAgentConnection(client, () => { }, Wait);
        int drops = 0;
        conn.Disconnected += () => Interlocked.Increment(ref drops);
        Assert.True(await conn.ConnectAsync());

        server.Dispose();
        await WaitUntil(() => Volatile.Read(ref drops) == 1);
        Assert.False(conn.IsConnected);

        using var again = Server(name);
        var got = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Received += m => got.TrySetResult(m);
        Assert.True(await conn.ConnectAsync());
        Assert.True(conn.IsConnected);
        await conn.SendAsync(new Hello("0.1.0+test"));
        Assert.IsType<Snapshot>(await got.Task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref drops));
    }

    [Fact]
    public async Task A_connection_the_Agent_drops_at_once_is_never_left_reported_as_connected()
    {
        // The server closes every client before its handshake, so the read loop may report the drop before ConnectAsync
        // has returned: the connection must still end up not connected (else the window would wait forever for a snapshot).
        var name = NewName();
        using var server = Server(name, new IpcServerOptions(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5)));
        await using var client = new IpcClient(name);
        var conn = new IpcAgentConnection(client, () => { }, Wait);
        int drops = 0;
        conn.Disconnected += () => Interlocked.Increment(ref drops);

        for (int i = 1; i <= 20; i++)
        {
            int before = Volatile.Read(ref drops);
            bool ok = await conn.ConnectAsync();
            if (ok) await WaitUntil(() => Volatile.Read(ref drops) > before);
            Assert.False(conn.IsConnected, $"attempt {i}: connected={ok}, still reported as connected after the drop");
        }
    }

    [Fact]
    public async Task LaunchAgent_runs_the_launcher()
    {
        await using var client = new IpcClient(NewName());
        int launches = 0;
        var conn = new IpcAgentConnection(client, () => launches++);
        conn.LaunchAgent();
        Assert.Equal(1, launches);
    }
}
