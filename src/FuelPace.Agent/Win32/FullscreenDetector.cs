using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace FuelPace.Agent.Win32;

/// <summary>
/// Exclusive fullscreen Direct3D (the overlay cannot be composed over it, Review Focus 1): the shell's
/// <c>SHQueryUserNotificationState</c> reports <c>QUNS_RUNNING_D3D_FULL_SCREEN</c>. Borderless windowed reports something else.
/// </summary>
public static class FullscreenDetector
{
    public static bool IsD3DFullscreenActive() =>
        PInvoke.SHQueryUserNotificationState(out var state).Succeeded
        && state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;

    /// <summary>The raw state for the system info (e.g. <c>QUNS_ACCEPTS_NOTIFICATIONS</c>), or the failing HRESULT.</summary>
    public static string Describe()
    {
        var hr = PInvoke.SHQueryUserNotificationState(out var state);
        return hr.Succeeded ? state.ToString() : $"error 0x{hr.Value:X8}";
    }
}
