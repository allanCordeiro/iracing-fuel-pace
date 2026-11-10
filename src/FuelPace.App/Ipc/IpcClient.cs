using System.IO.Pipes;
using FuelPace.App.Diagnostics;
using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Ipc;

/// <summary>Named-pipe client for the Agent's <see cref="IpcServer"/>. <see cref="Received"/> runs on a thread-pool thread.</summary>
public sealed class IpcClient : IAsyncDisposable
{
    readonly string _pipeName;
    readonly DiagLog _log;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    NamedPipeClientStream? _pipe;
    Task? _readLoop;
    int _disconnected, _disposing;

    public IpcClient(string pipeName, DiagLog? log = null)
    {
        _pipeName = pipeName;
        _log = log ?? DiagLog.Null;
    }

    public event Action<IpcMessage>? Received;
    public event Action? Disconnected;

    public async Task<bool> ConnectAsync(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(timeout);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            return false;
        }
        _pipe = pipe;
        _log.Emit(DiagLevel.Info, AppDiagEvents.IpcClient, 0, i0: 1);
        _readLoop = Task.Run(ReadLoopAsync);
        return true;
    }

    public async ValueTask SendAsync(IpcMessage m)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("not connected");
        var json = IpcJson.Serialize(m);
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try { await pipe.WriteAsync(line, _cts.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            RaiseDisconnected();
            throw;
        }
        finally { _writeLock.Release(); }
    }

    async Task ReadLoopAsync()
    {
        var reader = new IpcLineReader(_pipe!, IpcServer.MaxMessageBytes);
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0) continue;
                if (!IpcJson.TryDeserialize(line, out var msg, out var err))
                {
                    _log.Text(DiagLevel.Warn, "ipc", $"ignored message ({line.Length} bytes): {err}");
                    continue;
                }
                try { Received?.Invoke(msg!); }
                catch (Exception ex) { _log.Text(DiagLevel.Error, "ipc", $"Received handler failed: {ex}"); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException) { }
        finally { RaiseDisconnected(); }
    }

    void RaiseDisconnected()
    {
        if (Volatile.Read(ref _disposing) != 0) return;
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        _log.Emit(DiagLevel.Info, AppDiagEvents.IpcClient, 0, i0: 0);
        Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposing, 1);
        _cts.Cancel();
        _pipe?.Dispose();
        if (_readLoop != null) { try { await _readLoop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}
