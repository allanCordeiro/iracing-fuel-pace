using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Diagnostics;

public static class CrashHandler
{
    private static int _installed;

    public static void Install(DiagLog log, DiagFileSink sink)
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Handle(log, sink, e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => HandleUnobserved(log, sink, e.Exception);
    }

    /// <summary>A faulted task nobody awaited: the process goes on, so it is an Error of category <c>task</c>, not fatal.</summary>
    internal static void HandleUnobserved(DiagLog log, DiagFileSink sink, Exception ex)
    {
        log.Exception("task", ex);
        sink.FlushNow();
    }

    internal static void Handle(DiagLog log, DiagFileSink sink, Exception ex)
    {
        log.Exception("fatal", ex);
        sink.FlushNow();
    }
}
