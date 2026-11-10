using FuelPace.App.Settings;

namespace FuelPace.App.Tests.Settings;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F", 6, 0x46)]
    [InlineData("alt+f9", 1, 0x78)]
    [InlineData("Ctrl+Shift+1", 6, 0x31)]
    [InlineData("Win+Alt+F24", 9, 0x87)]
    [InlineData(" ctrl + shift + a ", 6, 0x41)]
    public void Parses_hotkeys(string s, int mods, int vk)
    {
        Assert.True(Hotkey.TryParse(s, out var key));
        Assert.Equal(mods, (int)key.Modifiers);
        Assert.Equal(vk, key.VirtualKey);
    }

    [Theory]
    [InlineData("F")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift+Enter2")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+Ctrl+F")]
    [InlineData("Ctrl+Shift")]
    [InlineData("")]
    [InlineData("+F")]
    public void Rejects_invalid(string s) => Assert.False(Hotkey.TryParse(s, out _));

    [Theory]
    [InlineData("shift+ctrl+f", "Ctrl+Shift+F")]
    [InlineData("alt+f9", "Alt+F9")]
    [InlineData("win+ctrl+alt+shift+0", "Ctrl+Alt+Shift+Win+0")]
    public void ToString_is_canonical_and_round_trips(string s, string canonical)
    {
        Assert.True(Hotkey.TryParse(s, out var key));
        Assert.Equal(canonical, key.ToString());
        Assert.True(Hotkey.TryParse(key.ToString(), out var again));
        Assert.Equal(key, again);
    }
}
