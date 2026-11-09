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
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < RetryDelaysMs.Length)
            {
                Thread.Sleep(RetryDelaysMs[attempt]);
            }
        }
    }
}
