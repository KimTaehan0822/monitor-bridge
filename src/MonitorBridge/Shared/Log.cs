namespace MonitorBridge.Shared;

/// <summary>Appends to %AppData%\MonitorBridge\log.txt (trimmed at 1 MB).</summary>
internal static class Log
{
    static readonly object Gate = new();
    static readonly string PathName = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorBridge", "log.txt");

    public static void Info(string s) => Write("INFO", s);
    public static void Error(Exception e) => Write("ERROR", e.ToString());

    static void Write(string level, string s)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
                if (File.Exists(PathName) && new FileInfo(PathName).Length > 1_000_000) File.Delete(PathName);
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {s}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break input handling */ }
    }
}
