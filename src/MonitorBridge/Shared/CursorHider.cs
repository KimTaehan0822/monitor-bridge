using MonitorBridge.Native;

namespace MonitorBridge.Shared;

/// <summary>
/// Hides the mouse pointer everywhere (including screen captures such as OBS) by swapping every system cursor
/// for a transparent one. Restore() reloads the user's cursor scheme.
/// </summary>
internal static class CursorHider
{
    static readonly uint[] Ids = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };
    static readonly object Gate = new();

    public static bool IsHidden { get; private set; }

    public static void Hide()
    {
        lock (Gate)
        {
            if (IsHidden) return;
            var and = Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray();
            var xor = new byte[32 * 32 / 8];
            foreach (var id in Ids)
            {
                // SetSystemCursor takes ownership and destroys the handle, so every id needs its own cursor.
                var c = Win32.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
                if (c != IntPtr.Zero) Win32.SetSystemCursor(c, id);
            }
            IsHidden = true;
        }
    }

    /// <summary>Always safe to call; also run at startup in case a previous run died while hidden.</summary>
    public static void Restore()
    {
        lock (Gate)
        {
            Win32.SystemParametersInfo(Win32.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            IsHidden = false;
        }
    }
}
