namespace FuelPace.Agent.Win32;

/// <summary>
/// One Agent per user session: the named mutex <c>Local\FuelPace.Agent</c>. The first instance keeps it open for its lifetime;
/// a second one finds it already created, asks the first by IPC to open the Settings window and exits with 0
/// (Review Focus 4). Only the mutex's existence matters (it is never acquired), so any thread may dispose it, and the OS
/// removes it when the owning process ends, however it ends.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = @"Local\FuelPace.Agent";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>The instance guard, or null when another instance already holds <paramref name="name"/>.</summary>
    public static SingleInstance? TryAcquire(string name = DefaultName)
    {
        var m = new Mutex(initiallyOwned: false, name, out bool createdNew);
        if (createdNew) return new SingleInstance(m);
        m.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}
