using System.Globalization;

namespace FuelPace.App.Settings;

/// <summary>Modifier bits; the values equal the Win32 <c>MOD_ALT/CONTROL/SHIFT/WIN</c> flags.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global hotkey: at least one modifier plus a key (A–Z, 0–9, F1–F24) as a Win32 virtual-key code.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, ushort VirtualKey)
{
    const ushort VkF1 = 0x70;

    /// <summary>Parses text such as <c>Ctrl+Shift+F</c> (case-insensitive, modifiers in any order, spaces around '+' ignored).</summary>
    public static bool TryParse(string? text, out Hotkey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+');
        if (parts.Length < 2) return false;

        var mods = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var m = parts[i].Trim().ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (m == HotkeyModifiers.None || (mods & m) != 0) return false;
            mods |= m;
        }

        if (!TryParseKey(parts[^1].Trim(), out var vk)) return false;
        key = new Hotkey(mods, vk);
        return true;
    }

    static bool TryParseKey(string k, out ushort vk)
    {
        vk = 0;
        if (k.Length == 1)
        {
            char c = char.ToUpperInvariant(k[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
            return false;
        }
        if (k.Length is 2 or 3 && (k[0] is 'F' or 'f')
            && int.TryParse(k.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            && n is >= 1 and <= 24 && k[1] != '0')
        {
            vk = (ushort)(VkF1 + n - 1);
            return true;
        }
        return false;
    }

    /// <summary>Canonical form, e.g. <c>Ctrl+Shift+F</c> (order Ctrl, Alt, Shift, Win).</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        if ((Modifiers & HotkeyModifiers.Ctrl) != 0) sb.Append("Ctrl+");
        if ((Modifiers & HotkeyModifiers.Alt) != 0) sb.Append("Alt+");
        if ((Modifiers & HotkeyModifiers.Shift) != 0) sb.Append("Shift+");
        if ((Modifiers & HotkeyModifiers.Win) != 0) sb.Append("Win+");
        if (VirtualKey is >= VkF1 and <= 0x87) sb.Append('F').Append((VirtualKey - VkF1 + 1).ToString(CultureInfo.InvariantCulture));
        else sb.Append((char)VirtualKey);
        return sb.ToString();
    }
}
