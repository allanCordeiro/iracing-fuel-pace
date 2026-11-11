namespace FuelPace.Core.Storage;

/// <summary>Write to <c>{path}.tmp</c>, then rename over the target. On Windows the rename fails while another process holds the target open, so it is retried.</summary>
public static class AtomicFile
{
    private static readonly int[] RetryDelaysMs = [50, 100, 200, 400, 800];

    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        Retry(() => { File.Move(tmp, path, overwrite: true); return 0; });
    }

    /// <summary>Reads the whole file without blocking writers or deleters, retrying while another process holds it exclusively.</summary>
    public static string ReadAllText(string path) => Retry(() => ReadOnce(path));

    internal static string ReadOnce(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>Runs <paramref name="action"/>, retrying with 50/100/200/400/800 ms backoff on IOException or UnauthorizedAccessException; the last failure is thrown.</summary>
    internal static T Retry<T>(Func<T> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < RetryDelaysMs.Length)
            {
                Thread.Sleep(RetryDelaysMs[attempt]);
            }
        }
    }
}
