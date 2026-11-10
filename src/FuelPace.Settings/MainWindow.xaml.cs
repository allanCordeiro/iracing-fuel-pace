using System.Windows;

namespace FuelPace.Settings;

/// <summary>The Settings window (mocks 7 and 8): its own title bar, the side menu and the selected page. No logic of its own:
/// everything binds to the <c>ShellViewModel</c> it is given as DataContext.</summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>A second launch asked for this window: restore it and bring it to the front (it was started by the user, so
    /// it may take the focus; the Agent's overlay never does).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true; // Activate alone may only flash the taskbar button when another window holds the foreground
        Topmost = false;
        Focus();
    }
}
