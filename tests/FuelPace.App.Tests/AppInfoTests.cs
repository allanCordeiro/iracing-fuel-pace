using FuelPace.App;

namespace FuelPace.App.Tests;

public class AppInfoTests
{
    [Fact]
    public void Version_is_informational_version() => Assert.Matches(@"^0\.1\.0\+", AppInfo.Version);

    [Fact]
    public void Debug_marker_detection_uses_exe_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuelpace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(AppInfo.DebugMarkerPresentIn(dir));
            File.WriteAllBytes(Path.Combine(dir, AppInfo.DebugMarkerFileName), []);
            Assert.True(AppInfo.DebugMarkerPresentIn(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
