using MonitorBridge.Core.Protocol;

namespace MonitorBridge.Core.Files;

/// <summary>
/// Writes an incoming file bundle under tempRoot\&lt;id&gt;. Entries arrive first; file contents then follow as one
/// byte stream in entry order. Paths from the peer are untrusted and must stay inside the folder.
/// </summary>
public sealed class FileReceiver
{
    readonly string _root;
    readonly Queue<(string Path, long Length)> _files = new();
    readonly HashSet<string> _top = new(StringComparer.OrdinalIgnoreCase);
    FileStream? _current;
    long _remaining;

    public FileReceiver(string tempRoot, Guid id)
    {
        _root = Path.GetFullPath(Path.Combine(tempRoot, id.ToString("N")));
        Directory.CreateDirectory(_root);
    }

    public void Add(FileEntry e)
    {
        var rel = e.RelativePath;
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel) || rel.Contains(':') || rel.StartsWith('\\') || rel.StartsWith('/')
            || rel.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || rel.Split('\\', '/').Any(seg => seg is ".." or "." || seg.Length == 0))
            throw new InvalidDataException($"Unsafe path: {rel}");
        var full = Path.GetFullPath(Path.Combine(_root, rel));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe path: {rel}");
        if (e.Length < 0) throw new InvalidDataException("Negative length");

        _top.Add(Path.Combine(_root, rel.Split('\\', '/')[0]));
        if (e.IsDirectory) { Directory.CreateDirectory(full); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (e.Length == 0) File.WriteAllBytes(full, Array.Empty<byte>());
        else _files.Enqueue((full, e.Length));
    }

    public void Write(byte[] chunk)
    {
        int offset = 0;
        while (offset < chunk.Length)
        {
            if (_current is null)
            {
                if (!_files.TryDequeue(out var next)) throw new InvalidDataException("More data than announced");
                _current = new FileStream(next.Path, FileMode.Create, FileAccess.Write, FileShare.None);
                _remaining = next.Length;
            }
            int n = (int)Math.Min(_remaining, chunk.Length - offset);
            _current.Write(chunk, offset, n);
            offset += n;
            _remaining -= n;
            if (_remaining == 0) { _current.Dispose(); _current = null; }
        }
    }

    /// <summary>Returns the top-level copied items, ready for a file-drop clipboard entry.</summary>
    public IReadOnlyList<string> Complete()
    {
        if (_current is not null || _files.Count > 0)
        {
            Cancel();
            throw new InvalidDataException("Transfer ended early");
        }
        return _top.ToList();
    }

    public void Cancel()
    {
        _current?.Dispose();
        _current = null;
        _files.Clear();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Hidden folder inside the save folder that holds files while they arrive.</summary>
    public const string StagingFolderName = ".MonitorBridge-받는중";

    /// <summary>A path in <paramref name="folder"/> for <paramref name="name"/> that is not taken: "a.mkv", then "a (2).mkv"…</summary>
    public static string UniqueName(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }

    /// <summary>Moves finished items into the save folder without overwriting or merging anything there.</summary>
    public static IReadOnlyList<string> MoveInto(IReadOnlyList<string> tops, string dest)
    {
        var moved = new List<string>();
        foreach (var src in tops)
        {
            for (int attempt = 0; ; attempt++)
            {
                var target = UniqueName(dest, Path.GetFileName(src));
                try
                {
                    if (Directory.Exists(src)) Directory.Move(src, target);
                    else File.Move(src, target);
                    moved.Add(target);
                    break;
                }
                catch (IOException) when (attempt < 5) { } // the name was taken just now; try the next number
            }
        }
        return moved;
    }

    public static void CleanupExcept(string tempRoot, Guid? keep)
    {
        if (!Directory.Exists(tempRoot)) return;
        var keepName = keep?.ToString("N");
        foreach (var d in Directory.GetDirectories(tempRoot))
        {
            if (Path.GetFileName(d) == keepName) continue;
            try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
