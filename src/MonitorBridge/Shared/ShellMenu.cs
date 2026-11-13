using Microsoft.Win32;

namespace MonitorBridge.Shared;

/// <summary>
/// The Explorer right-click item that pulls files copied on the other PC into a folder. Classic verbs only:
/// on Windows 11 they live under "더 많은 옵션 표시" or Shift+right-click (the new menu needs a signed package).
/// </summary>
internal static class ShellMenu
{
    public const string Text = "Monitor Bridge: 상대 PC에서 복사한 파일 여기에 붙여넣기";
    const string Verb = "MonitorBridge.Paste";
    // Folder background, a folder icon, and the desktop.
    static readonly string[] Places = { @"Directory\Background", "Directory", "DesktopBackground" };

    /// <summary>Per-user, no admin needed; re-run at every start so a moved exe stays right.</summary>
    public static void Register(string exePath)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        Register(classes, exePath);
    }

    public static void Register(RegistryKey classesRoot, string exePath)
    {
        foreach (var place in Places)
        {
            using var verb = classesRoot.CreateSubKey($@"{place}\shell\{Verb}");
            verb.SetValue("", Text);
            verb.SetValue("Icon", $"\"{exePath}\",0");
            using var cmd = verb.CreateSubKey("command");
            cmd.SetValue("", $"\"{exePath}\" --paste \"%V\"");
        }
    }

    public static void Unregister(RegistryKey classesRoot)
    {
        foreach (var place in Places) classesRoot.DeleteSubKeyTree($@"{place}\shell\{Verb}", false);
    }
}

internal static class PasteArgs
{
    /// <summary>
    /// The folder after --paste. Explorer passes a drive root as "D:\" and the backslash escapes the closing
    /// quote, so it arrives as D:" — strip the quote and put the root backslash back.
    /// </summary>
    public static string? Folder(string[] args)
    {
        int i = Array.IndexOf(args, "--paste");
        if (i < 0 || i + 1 >= args.Length) return null;
        var f = args[i + 1].Trim().TrimEnd('"').Trim();
        if (f.Length == 2 && f[1] == ':') f += @"\";
        return f.Length == 0 ? null : f;
    }
}

/// <summary>
/// Hands a right-clicked folder from the short-lived --paste process to the running instance through a file in
/// the settings folder. A file works across elevation (the stream PC runs as admin, Explorer's launch does not);
/// a pipe or window message from a lower-integrity process would be blocked.
/// </summary>
internal static class PasteChannel
{
    const string FileName = "paste-request.txt";
    public static string DefaultDir => Path.GetDirectoryName(Core.Config.AppConfig.DefaultPath)!;

    public static IDisposable Start(string dir, Action<string> onFolder)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        try { File.Delete(path); } catch (IOException) { } // a request left from before this run is stale
        var w = new FileSystemWatcher(dir, FileName) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        void Take()
        {
            string folder;
            try
            {
                folder = File.ReadAllText(path).Trim();
                File.Delete(path); // tells the --paste process it was picked up
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // not complete yet / already taken
            if (folder.Length > 0) onFolder(folder);
        }
        w.Created += (_, _) => Take();
        w.Renamed += (_, _) => Take();
        w.Changed += (_, _) => Take();
        w.EnableRaisingEvents = true;
        return w;
    }

    /// <summary>True once the running instance has taken the request.</summary>
    public static bool TrySend(string dir, string folder, int timeoutMs = 3000)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FileName);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, folder);
        File.Move(tmp, path, overwrite: true); // appears whole, never half-written
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (!File.Exists(path)) return true;
            Thread.Sleep(50);
        }
        try { File.Delete(path); } catch (IOException) { }
        return false;
    }
}
