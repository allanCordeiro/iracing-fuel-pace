using System.IO.Pipes;
using System.Threading.Channels;
using FuelPace.App.Diagnostics;
using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Ipc;

/// <summary>Handed to the message handler so it can answer the client the message came from.</summary>
public sealed class IpcServerContext
{
    readonly IpcServer.Client _client;
    internal IpcServerContext(IpcServer.Client client) => _client = client;

    public int ClientId => _client.Id;

    /// <summary>Queues a message for this client only. Never blocks; a client whose queue overflows is disconnected.</summary>
    public ValueTask ReplyAsync(IpcMessage m)
    {
        _client.TryEnqueue(IpcJson.Serialize(m));
        return ValueTask.CompletedTask;
    }
}

/// <param name="HandshakeTimeout">The first complete line must arrive this long after accept.</param>
/// <param name="PartialLineTimeout">Once bytes of a line are buffered, the line must complete within this time. No idle timeout after the handshake.</param>
public sealed record IpcServerOptions(TimeSpan HandshakeTimeout, TimeSpan PartialLineTimeout)
{
    public static IpcServerOptions Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
}

/// <summary>
/// Named-pipe server (current user only), newline-delimited JSON. At most <see cref="MaxClients"/> clients; a message larger than
/// <see cref="MaxMessageBytes"/> disconnects its sender; unparsable or unknown messages are logged and skipped.
/// </summary>
public sealed class IpcServer : IDisposable
{
    public const int MaxClients = 4;
    public const int MaxMessageBytes = 8 * 1024 * 1024;
    internal const int SendQueueCapacity = 256;

    readonly string _pipeName;
    readonly Func<IpcMessage, IpcServerContext, ValueTask> _handler;
    readonly DiagLog _log;
    readonly IpcServerOptions _options;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _slots = new(MaxClients, MaxClients);
    readonly List<Client> _clients = [];
    readonly Lock _gate = new();
    int _nextId;
    bool _started, _disposed;
    Task? _acceptLoop;

    public IpcServer(string pipeName, Func<IpcMessage, IpcServerContext, ValueTask> handler, DiagLog log, IpcServerOptions? options = null)
    {
        _options = options ?? IpcServerOptions.Default;
        _pipeName = pipeName;
        _handler = handler;
        _log = log ?? DiagLog.Null;
    }

    public int ClientCount { get { using (_gate.EnterScope()) return _clients.Count; } }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Serializes once and queues the bytes for every client; never waits on a client's pipe.</summary>
    public ValueTask BroadcastAsync(IpcMessage m)
    {
        Client[] snapshot;
        using (_gate.EnterScope()) snapshot = [.. _clients];
        if (snapshot.Length == 0) return ValueTask.CompletedTask;
        var bytes = IpcJson.Serialize(m);
        foreach (var c in snapshot) c.TryEnqueue(bytes);
        return ValueTask.CompletedTask;
    }

    async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        var backoff = TimeSpan.FromMilliseconds(250);
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            bool slotHeld = false;
            try
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                slotHeld = true;
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var client = new Client(this, pipe, Interlocked.Increment(ref _nextId));
                using (_gate.EnterScope()) _clients.Add(client);
                _log.Emit(DiagLevel.Info, AppDiagEvents.IpcClient, 0, i0: 1, i1: client.Id);
                slotHeld = false; // released by the client when it ends
                backoff = TimeSpan.FromMilliseconds(250);
                client.Run();
            }
            catch (OperationCanceledException) { pipe?.Dispose(); if (slotHeld) _slots.Release(); return; }
            catch (Exception ex)
            {
                pipe?.Dispose();
                if (slotHeld) _slots.Release();
                if (_cts.IsCancellationRequested) return;
                _log.Text(DiagLevel.Warn, "ipc", "accept failed: " + ex.Message);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 5000));
            }
        }
    }

    void Remove(Client c)
    {
        bool removed;
        using (_gate.EnterScope()) removed = _clients.Remove(c);
        if (!removed) return;
        _log.Emit(DiagLevel.Info, AppDiagEvents.IpcClient, 0, i0: 0, i1: c.Id);
        if (!_disposed) _slots.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        Client[] all;
        using (_gate.EnterScope()) all = [.. _clients];
        foreach (var c in all) c.Close();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }

    internal sealed class Client
    {
        readonly IpcServer _server;
        readonly NamedPipeServerStream _pipe;
        readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        readonly CancellationTokenSource _cts;
        readonly CancellationToken _ct;
        int _closed;

        public int Id { get; }

        public Client(IpcServer server, NamedPipeServerStream pipe, int id)
        {
            _server = server; _pipe = pipe; Id = id;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(server._cts.Token);
            _ct = _cts.Token;
        }

        public void Run()
        {
            _ = Task.Run(WriteLoopAsync);
            _ = Task.Run(ReadLoopAsync);
        }

        public void TryEnqueue(byte[] json)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            var line = new byte[json.Length + 1];
            json.CopyTo(line, 0);
            line[^1] = (byte)'\n';
            if (!_queue.Writer.TryWrite(line))
            {
                _server._log.Text(DiagLevel.Warn, "ipc", $"client {Id} send queue full; disconnecting");
                Close();
            }
        }

        async Task ReadLoopAsync()
        {
            var ctx = new IpcServerContext(this);
            var reader = new IpcLineReader(_pipe, MaxMessageBytes);
            bool handshake = true;
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(_ct, handshake ? _server._options.HandshakeTimeout : null, _server._options.PartialLineTimeout).ConfigureAwait(false);
                    handshake = false;
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    if (!IpcJson.TryDeserialize(line, out var msg, out var err))
                    {
                        _server._log.Text(DiagLevel.Warn, "ipc", $"client {Id}: ignored message ({line.Length} bytes): {err}");
                        continue;
                    }
                    try { await _server._handler(msg!, ctx).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!_ct.IsCancellationRequested)
                    {
                        _server._log.Text(DiagLevel.Debug, "ipc", $"handler for {msg!.GetType().Name} was canceled");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _server._log.Text(DiagLevel.Error, "ipc", $"handler failed for {msg!.GetType().Name}: {ex}");
                    }
                }
            }
            catch (TimeoutException)
            {
                _server._log.Text(DiagLevel.Warn, "ipc", $"client {Id}: read deadline exceeded; disconnecting");
            }
            catch (InvalidDataException)
            {
                _server._log.Text(DiagLevel.Warn, "ipc", $"client {Id}: message over {MaxMessageBytes} bytes; disconnecting");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            catch (Exception ex) { _server._log.Text(DiagLevel.Error, "ipc", $"client {Id} read failed: {ex}"); }
            finally { Close(); }
        }

        async Task WriteLoopAsync()
        {
            try
            {
                await foreach (var line in _queue.Reader.ReadAllAsync(_ct).ConfigureAwait(false))
                    await _pipe.WriteAsync(line, _ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
            catch (Exception ex) { _server._log.Text(DiagLevel.Error, "ipc", $"client {Id} write failed: {ex}"); }
            finally { Close(); }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _queue.Writer.TryComplete();
            try { _cts.Cancel(); _cts.Dispose(); } catch (ObjectDisposedException) { }
            try { _pipe.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            _server.Remove(this);
        }
    }
}
