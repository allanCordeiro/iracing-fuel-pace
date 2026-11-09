using FuelPace.Core.Storage;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Storage;

public class AtomicFileTests
{
    [Fact]
    public void Writes_and_replaces_without_temp_leftover()
    {
        var path = Fixtures.TempPath(".json");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        File.Delete(path);
    }

    [Fact]
    public void Creates_missing_directory()
    {
        var path = Path.Combine(Fixtures.TempPath(""), "sub", "f.json");
        AtomicFile.WriteAllText(path, "x");
        Assert.Equal("x", File.ReadAllText(path));
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, true);
    }
}
