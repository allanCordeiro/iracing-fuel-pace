namespace FuelPace.Tests.Support;

public static class Fixtures
{
    public const string ZolderFileName = "ligierjsp320_zolder_alt_2026-10-29_20-03-40.ibt";

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string RequireZolder()
    {
        var env = Environment.GetEnvironmentVariable("FUELPACE_FIXTURE_IBT");
        var path = !string.IsNullOrEmpty(env) ? env : Path.Combine(RepoRoot, "tests", "fixtures", ZolderFileName);
        Assert.SkipUnless(File.Exists(path), "fixture Zolder ausente");
        return path;
    }

    public static string TempPath(string ext)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuelpace-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FuelPace.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("FuelPace.sln not found above " + AppContext.BaseDirectory);
    }
}
