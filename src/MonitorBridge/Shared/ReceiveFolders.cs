using System.Runtime.InteropServices;
using MonitorBridge.Core.Config;

namespace MonitorBridge.Shared;

/// <summary>Where received files go: the configured folder, or the Downloads folder (wherever the user moved it).</summary>
internal static class ReceiveFolders
{
    static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static string Downloads()
    {
        if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var p) == 0)
        {
            try { return Marshal.PtrToStringUni(p)!; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    public static string Resolve(AppConfig cfg) => string.IsNullOrWhiteSpace(cfg.ReceiveFolder) ? Downloads() : cfg.ReceiveFolder;
}
