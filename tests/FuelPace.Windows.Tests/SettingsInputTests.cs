using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FuelPace.App.Ipc;
using FuelPace.App.Settings;
using FuelPace.App.SettingsUi;
using FuelPace.App.SettingsUi.ViewModels;
using FuelPace.Settings;
using FuelPace.Settings.Views;

namespace FuelPace.Windows.Tests;

/// <summary>What the pilot types into the Overlay page reaches the view model (and the Agent) as intended.</summary>
public sealed class SettingsInputTests
{
    sealed class Rig
    {
        public readonly FakeAgentConnection Conn = new();
        public readonly ManualScheduler Sched = new();
        public readonly ShellViewModel Vm;
        public readonly OverlayPage Page;

        public Rig()
        {
            Vm = new ShellViewModel(Conn, new ImmediateSynchronizationContext(), Sched, new NoShell(), new WpfTextMeasurer(EmbeddedFonts.Shared));
            Vm.ConnectAsync().GetAwaiter().GetResult();
            Conn.Raise(new Snapshot(SettingsMockData.Snapshot()));
            Page = new OverlayPage { DataContext = Vm.Overlay };
            Page.Measure(new Size(893, 777));
            Page.Arrange(new Rect(0, 0, 893, 777));
            Page.UpdateLayout();
        }

        public TextBox Box(string name) => (TextBox)Page.FindName(name);

        /// <summary>Types <paramref name="text"/> and leaves the field (the binding updates on lost focus).</summary>
        public void Type(TextBox box, string text)
        {
            box.Text = text;
            box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        }
    }

    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    [Fact]
    public void Decimals_accept_a_comma_or_a_point_and_show_a_point()
    {
        Sta.Run(() =>
        {
            var r = new Rig();
            var reserve = r.Box("Reserve");
            Assert.Equal("0.5", reserve.Text);

            r.Type(reserve, "0,8");
            Assert.Equal(0.8, r.Vm.Overlay.ReserveL);
            Assert.False(Validation.GetHasError(reserve));

            r.Type(reserve, "1.5");
            Assert.Equal(1.5, r.Vm.Overlay.ReserveL);

            r.Type(r.Box("Saving"), "4,5");
            Assert.Equal(4.5, r.Vm.Overlay.MaxSavingPercent);
        });
    }

    [Fact]
    public void Text_that_is_not_a_number_is_flagged_and_changes_nothing()
    {
        Sta.Run(() =>
        {
            var r = new Rig();
            var reserve = r.Box("Reserve");
            r.Type(reserve, "abc");
            Assert.Equal(0.5, r.Vm.Overlay.ReserveL);
            Assert.True(Validation.GetHasError(reserve));

            var x = r.Box("PosX");
            r.Type(x, "18,5");
            Assert.Equal(1840, r.Vm.Overlay.OffsetX);
            Assert.True(Validation.GetHasError(x));
            r.Type(x, "1700");
            Assert.Equal(1700, r.Vm.Overlay.OffsetX);
            Assert.False(Validation.GetHasError(x));
        });
    }

    [Fact]
    public void Typing_a_new_shortcut_sends_it_to_the_Agent()
    {
        Sta.Run(() =>
        {
            var r = new Rig();
            var key = Descendants<TextBox>(r.Page).Single(t => AutomationProperties.GetName(t) == r.Vm.Overlay.ToggleEdit.Label);
            Assert.Equal(HotkeySettings.DefaultToggleEdit, key.Text);

            key.Text = "Ctrl+Alt+G"; // updates as the pilot types
            r.Sched.FireAll(); // the 300 ms debounce
            var sent = Assert.Single(r.Conn.Sent.OfType<UpdateSettings>());
            Assert.Equal("Ctrl+Alt+G", sent.Settings.Hotkeys.ToggleEdit);
            Assert.Equal(1840, sent.Settings.Overlay.OffsetX); // built on the Agent's settings
        });
    }
}
