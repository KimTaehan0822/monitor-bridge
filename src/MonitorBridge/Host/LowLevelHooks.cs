using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MonitorBridge.Core.Config;
using MonitorBridge.Core.Protocol;
using MonitorBridge.Native;
using MonitorBridge.Shared;

namespace MonitorBridge.Host;

internal abstract record HookEvent;
internal sealed record HookMouseMove(int X, int Y) : HookEvent;
internal sealed record HookMouseButton(MouseButtonKind Button, bool Down) : HookEvent;
internal sealed record HookMouseWheel(bool Horizontal, int Delta) : HookEvent;
internal sealed record HookKey(int Vk, ushort Scan, bool Extended, bool Down) : HookEvent;

/// <summary>
/// One dedicated thread with a message loop that owns the low-level hooks and the lock hotkey.
/// Everything that touches the router runs on this thread (use <see cref="Invoke"/> from elsewhere).
/// </summary>
internal sealed class LowLevelHooks : IDisposable
{
    const uint MsgInvoke = Win32.WM_APP + 1;
    const int HotkeyId = 1;

    readonly Func<HookEvent, bool> _handler;
    readonly ConcurrentQueue<Action> _queue = new();
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    uint _threadId;
    IntPtr _mouseHook, _keyHook;
    // Keep delegates alive: the OS holds raw pointers to them.
    readonly Win32.HookProc _mouseProc, _keyProc;

    public event Action? HotkeyPressed;
    int _lastCallbackTick = Environment.TickCount;
    /// <summary>Environment.TickCount of the last hook callback (injected events included).</summary>
    public int LastCallbackTick => Volatile.Read(ref _lastCallbackTick);
    public bool Installed => _mouseHook != IntPtr.Zero;

    public LowLevelHooks(Func<HookEvent, bool> handler)
    {
        _handler = handler;
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
        _thread = new Thread(Loop) { IsBackground = true, Name = "MonitorBridge hooks", Priority = ThreadPriority.Highest };
        _thread.Start();
        _ready.Wait();
    }

    public void Invoke(Action a)
    {
        _queue.Enqueue(a);
        Win32.PostThreadMessage(_threadId, MsgInvoke, IntPtr.Zero, IntPtr.Zero);
    }

    public void Install() => Invoke(() => { if (!Installed) Reinstall(); });

    public void Uninstall() => Invoke(UninstallNow);

    /// <summary>Hook thread only.</summary>
    public void Reinstall()
    {
        UninstallNow();
        var mod = Win32.GetModuleHandle(null);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyProc, mod, 0);
        Volatile.Write(ref _lastCallbackTick, Environment.TickCount);
    }

    void UninstallNow()
    {
        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    /// <summary>Must be called on the hook thread (inside Invoke or a handler).</summary>
    public bool RegisterHotkey(Hotkey h) =>
        Win32.RegisterHotKey(IntPtr.Zero, HotkeyId,
            (h.Ctrl ? Win32.MOD_CONTROL : 0) | (h.Alt ? Win32.MOD_ALT : 0) | (h.Shift ? Win32.MOD_SHIFT : 0) | (h.Win ? Win32.MOD_WIN : 0) | Win32.MOD_NOREPEAT,
            (uint)h.VirtualKey);

    public void UnregisterHotkey() => Win32.UnregisterHotKey(IntPtr.Zero, HotkeyId);

    void Loop()
    {
        _threadId = Win32.GetCurrentThreadId();
        _ready.Set();
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == MsgInvoke)
                while (_queue.TryDequeue(out var a)) Safe(a);
            else if (msg.message == Win32.WM_HOTKEY)
                Safe(() => HotkeyPressed?.Invoke());
        }
        UninstallNow();
        UnregisterHotkey();
    }

    static void Safe(Action a)
    {
        try { a(); } catch (Exception e) { Log.Error(e); }
    }

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        Volatile.Write(ref _lastCallbackTick, Environment.TickCount);
        if (code >= 0)
        {
            var s = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            if ((s.flags & Win32.LLMHF_INJECTED) == 0)
            {
                HookEvent? e = (int)wParam switch
                {
                    Win32.WM_MOUSEMOVE => new HookMouseMove(s.pt.X, s.pt.Y),
                    Win32.WM_LBUTTONDOWN => new HookMouseButton(MouseButtonKind.Left, true),
                    Win32.WM_LBUTTONUP => new HookMouseButton(MouseButtonKind.Left, false),
                    Win32.WM_RBUTTONDOWN => new HookMouseButton(MouseButtonKind.Right, true),
                    Win32.WM_RBUTTONUP => new HookMouseButton(MouseButtonKind.Right, false),
                    Win32.WM_MBUTTONDOWN => new HookMouseButton(MouseButtonKind.Middle, true),
                    Win32.WM_MBUTTONUP => new HookMouseButton(MouseButtonKind.Middle, false),
                    Win32.WM_XBUTTONDOWN => new HookMouseButton((s.mouseData >> 16) == 1 ? MouseButtonKind.X1 : MouseButtonKind.X2, true),
                    Win32.WM_XBUTTONUP => new HookMouseButton((s.mouseData >> 16) == 1 ? MouseButtonKind.X1 : MouseButtonKind.X2, false),
                    Win32.WM_MOUSEWHEEL => new HookMouseWheel(false, (short)(s.mouseData >> 16)),
                    Win32.WM_MOUSEHWHEEL => new HookMouseWheel(true, (short)(s.mouseData >> 16)),
                    _ => null,
                };
                if (e is not null && Handle(e)) return 1;
            }
        }
        return Win32.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
    {
        Volatile.Write(ref _lastCallbackTick, Environment.TickCount);
        if (code >= 0)
        {
            var s = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            if ((s.flags & Win32.LLKHF_INJECTED) == 0)
            {
                int m = (int)wParam;
                bool down = m is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                var e = new HookKey((int)s.vkCode, (ushort)s.scanCode, (s.flags & Win32.LLKHF_EXTENDED) != 0, down);
                if (Handle(e)) return 1;
            }
        }
        return Win32.CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    bool Handle(HookEvent e)
    {
        try { return _handler(e); }
        catch (Exception ex) { Log.Error(ex); return false; }
    }

    public void Dispose()
    {
        Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
    }
}
