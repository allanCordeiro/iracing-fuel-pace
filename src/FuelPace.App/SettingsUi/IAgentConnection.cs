using FuelPace.App.Ipc;

namespace FuelPace.App.SettingsUi;

/// <summary>The Settings window's link to the Agent. The real one wraps an <see cref="IpcClient"/>; tests use a fake.
/// <see cref="Received"/> and <see cref="Disconnected"/> may be raised from any thread.</summary>
public interface IAgentConnection
{
    Task<bool> ConnectAsync();
    ValueTask SendAsync(IpcMessage m);
    event Action<IpcMessage>? Received;
    event Action? Disconnected;
    bool IsConnected { get; }
    /// <summary>Starts <c>FuelPace.Agent.exe</c> (it is the one that owns the pipe).</summary>
    void LaunchAgent();
}

/// <summary>Where view-model state changes run (the WPF dispatcher; synchronous in tests).</summary>
public interface ISynchronizationContext
{
    void Post(Action action);
}

public sealed class ImmediateSynchronizationContext : ISynchronizationContext
{
    public void Post(Action action) => action();
}

/// <summary>One-shot timers (debounce, reconnect). The callback may run on any thread; dispose to cancel.</summary>
public interface IDelayScheduler
{
    IDisposable Schedule(TimeSpan delay, Action action);
}

public sealed class TimerDelayScheduler : IDelayScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action action) => new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan);
}

/// <summary>What the window asks of the OS: open a folder in the file manager.</summary>
public interface IShellServices
{
    string LogsFolder { get; }
    void OpenFolder(string path);
}

/// <summary><see cref="IAgentConnection"/> over the named-pipe client.</summary>
public sealed class IpcAgentConnection : IAgentConnection
{
    readonly IpcClient _client;
    readonly Action _launch;
    readonly TimeSpan _timeout;
    volatile bool _connected;
    int _drops;

    public IpcAgentConnection(IpcClient client, Action launchAgent, TimeSpan? connectTimeout = null)
    {
        _client = client;
        _launch = launchAgent;
        _timeout = connectTimeout ?? TimeSpan.FromMilliseconds(500);
        _client.Received += m => Received?.Invoke(m);
        _client.Disconnected += () =>
        {
            Interlocked.Increment(ref _drops);
            _connected = false;
            Disconnected?.Invoke();
        };
    }

    public event Action<IpcMessage>? Received;
    public event Action? Disconnected;
    public bool IsConnected => _connected;

    /// <summary>A link the Agent dropped before this returns (its read loop already reported it) counts as not connected.</summary>
    public async Task<bool> ConnectAsync()
    {
        int drops = Volatile.Read(ref _drops);
        bool ok = await _client.ConnectAsync(_timeout).ConfigureAwait(false);
        _connected = ok;
        if (ok && Volatile.Read(ref _drops) != drops) _connected = ok = false;
        return ok;
    }
    public ValueTask SendAsync(IpcMessage m) => _client.SendAsync(m);
    public void LaunchAgent() => _launch();
}

/// <summary>Fire-and-forget sends: a failed send means the Agent went away, which the connection reports as Disconnected.</summary>
internal sealed class AgentSender(IAgentConnection connection)
{
    public bool IsConnected => connection.IsConnected;

    public void Send(IpcMessage m)
    {
        if (!connection.IsConnected) return;
        _ = SendCoreAsync(m);
    }

    async Task SendCoreAsync(IpcMessage m)
    {
        try { await connection.SendAsync(m).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
    }
}
