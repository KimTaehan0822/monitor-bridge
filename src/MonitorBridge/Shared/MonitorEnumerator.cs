using System.Runtime.InteropServices;
using MonitorBridge.Core.Layout;
using MonitorBridge.Native;

namespace MonitorBridge.Shared;

internal static class MonitorEnumerator
{
    public static string PcId => Environment.MachineName;

    /// <summary>Physical-pixel monitor list. The process must be Per-Monitor DPI aware v2.</summary>
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(h, ref info)) return true;
            double scale = Win32.GetDpiForMonitor(h, 0 /* MDT_EFFECTIVE_DPI */, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
            var r = info.rcMonitor;
            list.Add(new MonitorInfo(PcId, info.szDevice, r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top, scale));
            return true;
        }, IntPtr.Zero);
        return list.OrderBy(m => m.OsX).ThenBy(m => m.OsY).ToList();
    }
}
