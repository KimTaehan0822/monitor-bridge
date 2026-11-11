using System.Runtime.InteropServices;
using MonitorBridge.Core.Protocol;
using MonitorBridge.Native;

namespace MonitorBridge.Receiver;

/// <summary>Injects input with SendInput and remembers what is held so it can all be released.</summary>
internal sealed class InputInjector
{
    readonly HashSet<(ushort, bool, ushort)> _keys = new();
    readonly HashSet<MouseButtonKind> _buttons = new();
    readonly object _gate = new();

    public void MoveTo(int osX, int osY)
    {
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN), vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN), vh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        int nx = (int)Math.Round((osX - vx) * 65535.0 / Math.Max(1, vw - 1));
        int ny = (int)Math.Round((osY - vy) * 65535.0 / Math.Max(1, vh - 1));
        Mouse(nx, ny, 0, Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK);
        // Absolute normalisation can land a pixel off; SendInput keeps apps' drag tracking, SetCursorPos fixes the pixel.
        if (Win32.GetCursorPos(out var p) && (p.X != osX || p.Y != osY)) Win32.SetCursorPos(osX, osY);
    }

    public void Button(MouseButtonKind b, bool down)
    {
        lock (_gate) { if (down) _buttons.Add(b); else _buttons.Remove(b); }
        var (flags, data) = b switch
        {
            MouseButtonKind.Left => (down ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP, 0u),
            MouseButtonKind.Right => (down ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP, 0u),
            MouseButtonKind.Middle => (down ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP, 0u),
            MouseButtonKind.X1 => (down ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP, Win32.XBUTTON1),
            _ => (down ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP, Win32.XBUTTON2),
        };
        Mouse(0, 0, data, flags);
    }

    public void Wheel(bool horizontal, int delta) =>
        Mouse(0, 0, unchecked((uint)delta), horizontal ? Win32.MOUSEEVENTF_HWHEEL : Win32.MOUSEEVENTF_WHEEL);

    // 한/영, 한자, Pause and NumLock cannot be told apart (or typed at all) by scan code alone.
    static readonly HashSet<ushort> VkOnly = new() { 0x15, 0x19, 0x13, 0x90 };

    public void Key(ushort scan, bool extended, bool down, ushort vk = 0)
    {
        lock (_gate) { if (down) _keys.Add((scan, extended, vk)); else _keys.Remove((scan, extended, vk)); }
        bool byVk = vk != 0 && (scan == 0 || VkOnly.Contains(vk));
        uint flags = (byVk ? 0 : Win32.KEYEVENTF_SCANCODE) | (extended ? Win32.KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : Win32.KEYEVENTF_KEYUP);
        var input = new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = byVk ? vk : (ushort)0, wScan = scan, dwFlags = flags } } };
        Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
    }

    public void ReleaseAll()
    {
        (ushort, bool, ushort)[] keys;
        MouseButtonKind[] buttons;
        lock (_gate) { keys = _keys.ToArray(); buttons = _buttons.ToArray(); }
        foreach (var (s, e, v) in keys) Key(s, e, false, v);
        foreach (var b in buttons) Button(b, false);
    }

    static void Mouse(int dx, int dy, uint data, uint flags)
    {
        var input = new Win32.INPUT { type = Win32.INPUT_MOUSE, u = new Win32.InputUnion { mi = new Win32.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };
        Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
    }
}
