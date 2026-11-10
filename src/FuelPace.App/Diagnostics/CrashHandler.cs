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
        TaskScheduler.UnobservedTaskException += (_, e) => Handle(log, sink, e.Exception);
    }

    internal static void Handle(DiagLog log, DiagFileSink sink, Exception ex)
    {
        log.Exception("fatal", ex);
        sink.FlushNow();
    }
}
