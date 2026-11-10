using System.Text.RegularExpressions;

namespace FuelPace.App.Diagnostics;

/// <summary>
/// Logs and bundles may be shared publicly: the user-profile path becomes <c>%USERPROFILE%</c> and the user name
/// <c>%USERNAME%</c>. Applied where log text is written (<see cref="DiagFileSink"/>), again when a bundle copies the logs, and
/// to the system info. Cold path; may allocate.
/// </summary>
public static class Privacy
{
    private sealed class Identity
    {
        public Identity(string profile, string userName)
        {
            Profile = profile.TrimEnd('\\', '/');
            ProfileSlashes = Profile.Replace('\\', '/');
            ProfileJson = Profile.Replace("\\", "\\\\");
            User = userName.Length >= 3
                ? new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(userName) + @"(?![\p{L}\p{N}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                : null;
        }

        public string Profile { get; }
        public string ProfileSlashes { get; }
        /// <summary>The profile path as a JSON string literal spells it (backslashes doubled).</summary>
        public string ProfileJson { get; }
        public Regex? User { get; }
    }

    private static volatile Identity _id =
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);

    /// <summary>Replaces the user-profile path (either separator, any case) and the user name as a whole word.</summary>
    public static string Scrub(string text)
    {
        var id = _id;
        if (id.Profile.Length > 1)
        {
            text = text.Replace(id.Profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            if (!ReferenceEquals(id.ProfileSlashes, id.Profile))
                text = text.Replace(id.ProfileSlashes, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            if (!ReferenceEquals(id.ProfileJson, id.Profile))
                text = text.Replace(id.ProfileJson, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        return id.User?.Replace(text, "%USERNAME%") ?? text;
    }

    /// <summary>Tests: pretend <paramref name="profile"/> and <paramref name="userName"/> are the user's, until disposed.</summary>
    internal static IDisposable OverrideForTests(string profile, string userName)
    {
        var previous = _id;
        _id = new Identity(profile, userName);
        return new Restore(previous);
    }

    private sealed class Restore(Identity previous) : IDisposable
    {
        public void Dispose() => _id = previous;
    }
}
