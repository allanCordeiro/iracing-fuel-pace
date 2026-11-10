using FuelPace.Core.Strategy;

namespace FuelPace.App.Host;

/// <summary>Whether the overlay is on screen.</summary>
public static class AutoHidePolicy
{
    /// <summary>With auto-hide, an inactive overlay stays up this long before it hides (a pit stop flicker is not hidden).</summary>
    public static readonly TimeSpan InactiveDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Edit mode always shows. Otherwise the user's visibility toggle wins; then, with <paramref name="autoHide"/>, the
    /// overlay hides without the sim or after the state has been <see cref="SignalState.Inactive"/> for more than
    /// <see cref="InactiveDelay"/> in a row (<paramref name="inactiveFor"/>: how long so far; the caller keeps that time).
    /// </summary>
    public static bool ShouldShow(bool userVisible, bool autoHide, bool simConnected, in OverlayState s, bool editMode,
        TimeSpan inactiveFor)
    {
        if (editMode) return true;
        if (!userVisible) return false;
        if (!autoHide) return true;
        if (!simConnected) return false;
        return s.State != SignalState.Inactive || inactiveFor <= InactiveDelay;
    }
}
