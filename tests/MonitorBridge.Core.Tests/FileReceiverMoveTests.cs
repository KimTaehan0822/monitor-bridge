using MonitorBridge.Core.Files;
using MonitorBridge.Core.Protocol;

namespace MonitorBridge.Core.Tests;

public class FileReceiverMoveTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-move-" + Guid.NewGuid().ToString("N"));
    string Dest => Path.Combine(_dir, "dest");
    string Src => Path.Combine(_dir, "src");

    public FileReceiverMoveTests()
    {
        Directory.CreateDirectory(Dest);
        Directory.CreateDirectory(Src);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Make(string folder, string rel, string text = "x")
    {
        var p = Path.Combine(folder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void UniqueName_Free() =>
        Assert.Equal(Path.Combine(Dest, "a.mkv"), FileReceiver.UniqueName(Dest, "a.mkv"));

    [Fact]
    public void UniqueName_SkipsTaken()
    {
        Make(Dest, "a.mkv");
        Make(Dest, "a (2).mkv");
        Assert.Equal(Path.Combine(Dest, "a (3).mkv"), FileReceiver.UniqueName(Dest, "a.mkv"));
    }

    [Fact]
    public void UniqueName_NoExtension()
    {
        Make(Dest, "README");
        Assert.Equal(Path.Combine(Dest, "README (2)"), FileReceiver.UniqueName(Dest, "README"));
    }

    [Fact]
    public void MoveInto_FileCollision()
    {
        Make(Dest, "a.mkv", "old");
        var got = FileReceiver.MoveInto(new[] { Make(Src, "a.mkv", "new") }, Dest);
        Assert.Equal(new[] { Path.Combine(Dest, "a (2).mkv") }, got);
        Assert.Equal("new", File.ReadAllText(got[0]));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Dest, "a.mkv")));
    }

    [Fact]
    public void MoveInto_FolderCollision()
    {
        Make(Dest, @"dir\x.txt", "old");
        Make(Src, @"dir\y.txt", "new");
        var got = FileReceiver.MoveInto(new[] { Path.Combine(Src, "dir") }, Dest);
        Assert.Equal(new[] { Path.Combine(Dest, "dir (2)") }, got);
        Assert.True(File.Exists(Path.Combine(Dest, @"dir (2)\y.txt")));
        Assert.False(File.Exists(Path.Combine(Dest, @"dir\y.txt"))); // never merged into the existing folder
    }

    [Fact]
    public void Cancel_LeavesDestinationEmpty()
    {
        var staging = Path.Combine(Dest, FileReceiver.StagingFolderName);
        var rx = new FileReceiver(staging, Guid.NewGuid());
        rx.Add(new FileEntry(Guid.Empty, "big.bin", false, 100));
        rx.Write(new byte[40]);
        rx.Cancel();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Dest).Where(p => Path.GetFileName(p) != FileReceiver.StagingFolderName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Fact]
    public void MoveInto_LockedItem_StaysWhereItIs()
    {
        var src = Make(Src, "locked.mkv", "data");
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None)) // antivirus or indexer holding it
        {
            var moved = FileReceiver.MoveInto(new[] { src }, Dest, out var notMoved, retryDelayMs: 1);
            Assert.Empty(moved);
            Assert.Equal(new[] { src }, notMoved);
        }
        Assert.True(File.Exists(src)); // the received data is never thrown away
    }

    [Fact]
    public void CleanupExcept_SkipsKeptFolders()
    {
        var staging = Path.Combine(Dest, FileReceiver.StagingFolderName);
        var kept = new FileReceiver(staging, Guid.NewGuid());
        kept.Add(new FileEntry(Guid.Empty, "a.txt", false, 0));
        kept.KeepForUser();
        var junk = new FileReceiver(staging, Guid.NewGuid());
        junk.Add(new FileEntry(Guid.Empty, "b.txt", false, 0));
        FileReceiver.CleanupExcept(staging, null);
        Assert.True(File.Exists(Path.Combine(kept.Root, "a.txt")));
        Assert.False(Directory.Exists(junk.Root));
    }
}
