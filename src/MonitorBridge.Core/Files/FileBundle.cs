namespace MonitorBridge.Core.Files;

public sealed record FileEntryPlan(string FullPath, string RelativePath, bool IsDirectory, long Length);

/// <summary>Turns copied paths into an ordered entry list. Relative paths start at each copied item's own name.</summary>
public static class FileBundle
{
    public static (IReadOnlyList<FileEntryPlan> Entries, long TotalBytes) Plan(IEnumerable<string> paths)
    {
        var list = new List<FileEntryPlan>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var raw in paths)
        {
            var path = Path.TrimEndingDirectorySeparator(raw);
            var parent = Path.GetDirectoryName(path) ?? "";
            string Rel(string full) => Path.GetRelativePath(parent, full);
            if (Directory.Exists(path))
            {
                list.Add(new FileEntryPlan(path, Rel(path), true, 0));
                foreach (var d in Directory.EnumerateDirectories(path, "*", opts))
                    list.Add(new FileEntryPlan(d, Rel(d), true, 0));
                foreach (var f in Directory.EnumerateFiles(path, "*", opts))
                    if (TryLength(f) is { } len) list.Add(new FileEntryPlan(f, Rel(f), false, len));
            }
            else if (TryLength(path) is { } len)
            {
                list.Add(new FileEntryPlan(path, Rel(path), false, len));
            }
        }
        return (list, list.Sum(e => e.Length));
    }

    static long? TryLength(string f)
    {
        try { return new FileInfo(f).Length; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
