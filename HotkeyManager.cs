using System.Runtime.InteropServices;

namespace PhrasePaste;

/// <summary>Parsed global hotkey: modifier flags + virtual key.</summary>
internal readonly record struct HotkeyCombo(uint Modifiers, uint Vk, string Display);

/// <summary>Global hotkey registration via RegisterHotKey (same mechanism as Clippa).</summary>
internal static class HotkeyManager
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static readonly Dictionary<string, uint> KeyNames = BuildKeyNames();

    /// <summary>
    /// Parse a "Ctrl+Alt+1" style spec. Modifiers: Ctrl/Alt/Shift/Win (any order).
    /// The key itself is case-insensitive (a-z, 0-9, F1-F24, or a named key).
    /// </summary>
    public static bool TryParse(string? text, out HotkeyCombo combo, out string? error)
    {
        combo = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "hotkey is empty";
            return false;
        }

        uint mods = 0;
        string? keyPart = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.ToLowerInvariant();
            switch (part)
            {
                case "ctrl":
                case "control":
                case "ctl":
                    mods |= ModControl;
                    break;
                case "alt":
                    mods |= ModAlt;
                    break;
                case "shift":
                    mods |= ModShift;
                    break;
                case "win":
                case "windows":
                case "cmd":
                case "meta":
                    mods |= ModWin;
                    break;
                default:
                    if (keyPart is not null)
                    {
                        error = $"more than one key in '{text}'";
                        return false;
                    }
                    keyPart = raw;
                    break;
            }
        }

        if (keyPart is null)
        {
            error = $"no key in '{text}'";
            return false;
        }
        if (!KeyNames.TryGetValue(keyPart.ToLowerInvariant(), out var vk))
        {
            error = $"unknown key '{keyPart}'";
            return false;
        }

        combo = new HotkeyCombo(mods, vk, text);
        return true;
    }

    private static Dictionary<string, uint> BuildKeyNames()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < 26; i++) map[((char)('a' + i)).ToString()] = 0x41u + (uint)i;
        for (var i = 0; i < 10; i++) map[((char)('0' + i)).ToString()] = 0x30u + (uint)i;
        for (var i = 1; i <= 24; i++) map[$"f{i}"] = 0x70u + (uint)(i - 1);

        map["space"] = 0x20;
        map["enter"] = 0x0D; map["return"] = 0x0D;
        map["tab"] = 0x09;
        map["backspace"] = 0x08; map["back"] = 0x08;
        map["delete"] = 0x2E; map["del"] = 0x2E;
        map["insert"] = 0x2D; map["ins"] = 0x2D;
        map["home"] = 0x24;
        map["end"] = 0x23;
        map["pageup"] = 0x21; map["pgup"] = 0x21;
        map["pagedown"] = 0x22; map["pgdn"] = 0x22;
        map["escape"] = 0x1B; map["esc"] = 0x1B;
        map["up"] = 0x26;
        map["down"] = 0x28;
        map["left"] = 0x25;
        map["right"] = 0x27;
        map["printscreen"] = 0x2C; map["prtsc"] = 0x2C;
        map["pause"] = 0x13; map["break"] = 0x13;
        map["scrolllock"] = 0x91;
        map["capslock"] = 0x14;
        map["numlock"] = 0x90;
        map["plus"] = 0xBB; map["oemplus"] = 0xBB;
        map["minus"] = 0xBD; map["oemminus"] = 0xBD;
        map["period"] = 0xBE; map["dot"] = 0xBE; map["oemperiod"] = 0xBE;
        map["comma"] = 0xBC; map["oemcomma"] = 0xBC;
        map["slash"] = 0xBF; map["oem2"] = 0xBF;
        map["semicolon"] = 0xBA; map["oem1"] = 0xBA;
        map["apostrophe"] = 0xDE; map["quote"] = 0xDE; map["oem7"] = 0xDE;
        map["backslash"] = 0xDC; map["oem5"] = 0xDC;
        map["openbrackets"] = 0xDB; map["["] = 0xDB; map["oem4"] = 0xDB;
        map["closebrackets"] = 0xDD; map["]"] = 0xDD; map["oem6"] = 0xDD;
        map["tilde"] = 0xC0; map["`"] = 0xC0; map["oem3"] = 0xC0;
        map["num0"] = 0x60; map["num1"] = 0x61; map["num2"] = 0x62; map["num3"] = 0x63; map["num4"] = 0x64;
        map["num5"] = 0x65; map["num6"] = 0x66; map["num7"] = 0x67; map["num8"] = 0x68; map["num9"] = 0x69;

        return map;
    }
}
