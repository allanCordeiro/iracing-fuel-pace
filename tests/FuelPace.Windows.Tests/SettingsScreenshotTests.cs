using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FuelPace.App.Ipc;
using FuelPace.App.SettingsUi;
using FuelPace.App.SettingsUi.ViewModels;
using FuelPace.Settings;

namespace FuelPace.Windows.Tests;

/// <summary>
/// Each Settings page with the values of the approved mocks, rendered at 1120 × 820 (1x) to <c>artifacts/settings/&lt;page&gt;.png</c>
/// for review against mocks 7 and 8 (CI uploads them). The window is laid out and rendered off screen: its content is taken
/// out of the (never shown) window.
/// </summary>
public sealed class SettingsScreenshotTests
{
    const int W = 1120, H = 820;

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "FuelPace.sln"))) return d.FullName;
        throw new InvalidOperationException("FuelPace.sln not found above " + AppContext.BaseDirectory);
    }

    static ShellViewModel MockShell(FakeAgentConnection conn)
    {
        var vm = new ShellViewModel(conn, new ImmediateSynchronizationContext(), new NeverScheduler(), new NoShell(),
            new WpfTextMeasurer(EmbeddedFonts.Shared));
        vm.Profiles.TimeZone = TimeZoneInfo.Utc;
        vm.ConnectAsync().GetAwaiter().GetResult();
        conn.Raise(new Snapshot(SettingsMockData.Snapshot()));
        return vm;
    }

    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });

    /// <summary>Lays the window's content out at 1120 × 820 and renders it at 96 dpi; returns the BGRA pixels.</summary>
    static byte[] Render(ShellViewModel vm, string page)
    {
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        root.DataContext = vm;
        for (int i = 0; i < 3; i++)
        {
            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            root.UpdateLayout();
            Pump();
        }
        var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var dir = Path.Combine(RepoRoot(), "artifacts", "settings");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, page + ".png");
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using (var f = File.Create(path)) png.Save(f);
        Assert.True(new FileInfo(path).Length > 10_000, path + " is (nearly) empty");
        var px = new byte[W * H * 4];
        bmp.CopyPixels(px, W * 4, 0);
        return px;
    }

    static void AssertColor(byte[] px, int x, int y, uint rgb, string what)
    {
        int i = (y * W + x) * 4;
        (int r, int g, int b) = (px[i + 2], px[i + 1], px[i]);
        (int er, int eg, int eb) = ((int)(rgb >> 16) & 255, (int)(rgb >> 8) & 255, (int)rgb & 255);
        Assert.True(Math.Abs(r - er) <= 3 && Math.Abs(g - eg) <= 3 && Math.Abs(b - eb) <= 3,
            $"{what} at ({x},{y}): got #{r:X2}{g:X2}{b:X2}, expected #{rgb:X6}");
    }

    /// <summary>The chrome every page shares: title bar, side menu (with the active item), content background.</summary>
    static void AssertChrome(byte[] px, int activeItem)
    {
        AssertColor(px, 600, 4, 0x0C0E11, "title bar");
        AssertColor(px, 100, 780, 0x0F1114, "side menu");
        AssertColor(px, 180, 79 + activeItem * 41, 0x1E2632, "active menu item"); // 37 px items, 4 px apart, from y 62
        AssertColor(px, 240, 800, 0x121418, "content"); // the menu is 225 px wide (200 + padding + border, as the mock)
    }

    [Fact]
    public void Capture_page_matches_mock_7()
    {
        Sta.Run(() =>
        {
            var conn = new FakeAgentConnection();
            using var vm = MockShell(conn);
            vm.SelectedSection = "Captura";
            Assert.Equal("1:20.141", vm.Capture.TimeValue); // ruling A23: the mean of the fixture laps, not the mock's 1:20.140
            Assert.Equal(9, vm.Capture.Laps.Count);
            Assert.NotNull(vm.Capture.InProgress);
            var px = Render(vm, "captura");
            AssertChrome(px, 0);
        });
    }

    [Fact]
    public void Overlay_page_matches_mock_8()
    {
        Sta.Run(() =>
        {
            var conn = new FakeAgentConnection();
            using var vm = MockShell(conn);
            vm.SelectedSection = "Overlay e cálculo";
            Assert.Equal(1840, vm.Overlay.OffsetX);
            Assert.Equal(0, vm.Overlay.SelectedMonitorIndex);
            Assert.Equal("PRÉVIA · 100%", vm.Overlay.PreviewTitle);
            var px = Render(vm, "overlay");
            AssertChrome(px, 1);
            // The preview's stand-in for the game picture: the sky band above the overlay and the track below it.
            AssertColor(px, 1120 - 1 - 32 - 320 + 5, 105, 0x3A4552, "preview sky"); // left of the panel, above it
        });
    }

    [Fact]
    public void Profiles_page_renders()
    {
        Sta.Run(() =>
        {
            var conn = new FakeAgentConnection();
            using var vm = MockShell(conn);
            vm.SelectedSection = "Perfis";
            conn.Raise(SettingsMockData.Profiles());
            vm.Profiles.SelectedItem = vm.Profiles.Items[0];
            conn.Raise(SettingsMockData.Profile());
            Assert.Equal(2, vm.Profiles.Captures.Count);
            vm.Profiles.Captures[1].ApagarCommand.Execute(null); // the confirmation bar shows
            var px = Render(vm, "perfis");
            AssertChrome(px, 2);
        });
    }

    [Fact]
    public void Diagnostics_page_renders()
    {
        Sta.Run(() =>
        {
            var conn = new FakeAgentConnection();
            using var vm = MockShell(conn);
            vm.SelectedSection = "Diagnóstico";
            vm.Diagnostics.GerarPacoteCommand.Execute(null);
            conn.Raise(new BundleCreated(@"D:\Desktop\FuelPace-diag-20261110-201500.zip"));
            var px = Render(vm, "diagnostico");
            AssertChrome(px, 3);
        });
    }

    [Fact]
    public void Offline_banner_renders()
    {
        Sta.Run(() =>
        {
            var conn = new FakeAgentConnection();
            using var vm = MockShell(conn);
            conn.Drop();
            Assert.True(vm.ShowAgentOfflineBanner);
            var px = Render(vm, "captura-offline");
            AssertChrome(px, 0);
        });
    }
}
