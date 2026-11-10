using System.Windows;
using FuelPace.App;

namespace FuelPace.Settings;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "FuelPace " + AppInfo.Version;
    }
}
