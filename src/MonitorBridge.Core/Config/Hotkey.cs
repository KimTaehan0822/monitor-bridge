namespace MonitorBridge.Core.Config;

/// <summary>A key plus modifiers. Key names follow System.Windows.Forms.Keys.</summary>
public readonly record struct Hotkey(int VirtualKey, bool Ctrl, bool Alt, bool Shift, bool Win)
{
    static readonly Dictionary<string, int> Names = BuildNames();

    static Dictionary<string, int> BuildNames()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Scroll"] = 0x91, ["Pause"] = 0x13, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
            ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Space"] = 0x20, ["Tab"] = 0x09, ["Escape"] = 0x1B,
            ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
            ["Oem3"] = 0xC0, ["OemMinus"] = 0xBD, ["Oemplus"] = 0xBB, ["Oem5"] = 0xDC, ["Oemcomma"] = 0xBC, ["OemPeriod"] = 0xBE,
        };
        for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
        for (int i = 0; i <= 9; i++) { d["D" + i] = 0x30 + i; d["NumPad" + i] = 0x60 + i; }
        for (int i = 1; i <= 24; i++) d["F" + i] = 0x6F + i;
        return d;
    }

    public static bool TryParse(string s, out Hotkey h)
    {
        h = default;
        bool ctrl = false, alt = false, shift = false, win = false;
        int? vk = null;
        foreach (var part in s.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win": win = true; break;
                default:
                    if (vk is not null || !Names.TryGetValue(part, out var v)) return false;
                    vk = v;
                    break;
            }
        }
        if (vk is null) return false;
        h = new Hotkey(vk.Value, ctrl, alt, shift, win);
        return true;
    }

    public static string? NameOf(int vk) => Names.FirstOrDefault(kv => kv.Value == vk).Key;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(NameOf(VirtualKey) ?? $"0x{VirtualKey:X2}");
        return string.Join("+", parts);
    }
}

/// <summary>Feed every low-level key event; returns true on the press (not auto-repeat) of the hotkey with exactly its modifiers.</summary>
public sealed class HotkeyMatcher(Hotkey hotkey)
{
    bool _ctrl, _alt, _shift, _win, _targetDown;

    public Hotkey Hotkey { get; } = hotkey;

    /// <summary>Forget key state, e.g. after a lock screen hid the key-ups from the hook.</summary>
    public void Reset() => _ctrl = _alt = _shift = _win = _targetDown = false;

    public bool OnKey(int vk, bool down)
    {
        switch (vk)
        {
            case 0x11 or 0xA2 or 0xA3: _ctrl = down; return false;
            case 0x12 or 0xA4 or 0xA5: _alt = down; return false;
            case 0x10 or 0xA0 or 0xA1: _shift = down; return false;
            case 0x5B or 0x5C: _win = down; return false;
        }
        if (vk != Hotkey.VirtualKey) return false;
        if (!down) { _targetDown = false; return false; }
        if (_targetDown) return false;
        _targetDown = true;
        return _ctrl == Hotkey.Ctrl && _alt == Hotkey.Alt && _shift == Hotkey.Shift && _win == Hotkey.Win;
    }
}
