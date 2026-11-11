using System.Text;
using MonitorBridge.Core.Clipboard;
using MonitorBridge.Core.Files;
using MonitorBridge.Core.Protocol;

namespace MonitorBridge.Core.Tests;

public class ClipboardEchoTests
{
    [Fact]
    public void Echo_SameContent_IsEcho()
    {
        var e = new ClipboardEcho();
        e.MarkReceived(Encoding.UTF8.GetBytes("hi"));
        Assert.True(e.IsEcho(Encoding.UTF8.GetBytes("hi")));
    }

    [Fact]
    public void Echo_DifferentContent_NotEcho()
    {
        var e = new ClipboardEcho();
        e.MarkReceived(Encoding.UTF8.GetBytes("hi"));
        Assert.False(e.IsEcho(Encoding.UTF8.GetBytes("bye")));
        Assert.False(new ClipboardEcho().IsEcho(Array.Empty<byte>()));
    }
}

public class FileBundleTests : IDisposable
{
    readonly string _src = Directory.CreateTempSubdirectory("mb-src").FullName;
    readonly string _dst = Directory.CreateTempSubdirectory("mb-dst").FullName;
    public void Dispose() { Directory.Delete(_src, true); Directory.Delete(_dst, true); }

    string Make(string rel, string content)
    {
        var p = Path.Combine(_src, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Plan_FolderIncludesNestedFiles_WithRelativePaths()
    {
        Make(@"a\b\c.txt", "hello");
        Directory.CreateDirectory(Path.Combine(_src, @"a\empty"));
        var (entries, _) = FileBundle.Plan(new[] { Path.Combine(_src, "a") });
        var rels = entries.Select(e => (e.RelativePath, e.IsDirectory)).ToList();
        Assert.Contains(("a", true), rels);
        Assert.Contains((@"a\b", true), rels);
        Assert.Contains((@"a\empty", true), rels);
        Assert.Contains((@"a\b\c.txt", false), rels);
    }

    [Fact]
    public void Plan_TotalBytesSumsFiles()
    {
        var f1 = Make("x.txt", "12345");
        Make(@"d\y.txt", "123");
        var (_, total) = FileBundle.Plan(new[] { f1, Path.Combine(_src, "d") });
        Assert.Equal(8, total);
    }

    static void Feed(FileReceiver r, IEnumerable<FileEntryPlan> entries, Guid id)
    {
        foreach (var e in entries) r.Add(new FileEntry(id, e.RelativePath, e.IsDirectory, e.Length));
        foreach (var e in entries.Where(e => !e.IsDirectory))
        {
            var bytes = File.ReadAllBytes(e.FullPath);
            // Split across chunk boundaries to exercise file switching.
            for (int i = 0; i < bytes.Length; i += 2) r.Write(bytes.AsSpan(i, Math.Min(2, bytes.Length - i)).ToArray());
        }
    }

    [Fact]
    public void Receiver_WritesFilesAndReturnsTopLevel()
    {
        var f1 = Make("x.txt", "12345");
        Make(@"d\y.txt", "abc");
        Make(@"d\z.txt", "");
        var (entries, _) = FileBundle.Plan(new[] { f1, Path.Combine(_src, "d") });
        var id = Guid.NewGuid();
        var r = new FileReceiver(_dst, id);
        Feed(r, entries, id);
        var top = r.Complete();
        var root = Path.Combine(_dst, id.ToString("N"));
        Assert.Equal(new[] { Path.Combine(root, "x.txt"), Path.Combine(root, "d") }.Order(), top.Order());
        Assert.Equal("12345", File.ReadAllText(Path.Combine(root, "x.txt")));
        Assert.Equal("abc", File.ReadAllText(Path.Combine(root, @"d\y.txt")));
        Assert.Equal("", File.ReadAllText(Path.Combine(root, @"d\z.txt")));
    }

    [Theory]
    [InlineData(@"..\evil.txt")]
    [InlineData(@"a\..\..\evil.txt")]
    [InlineData(@"C:\evil.txt")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\rooted.txt")]
    [InlineData(@"a:b.txt")]
    [InlineData("")]
    public void Receiver_RejectsTraversal(string rel)
    {
        var r = new FileReceiver(_dst, Guid.NewGuid());
        Assert.Throws<InvalidDataException>(() => r.Add(new FileEntry(Guid.Empty, rel, false, 1)));
    }

    [Fact]
    public void Receiver_TooManyBytes_Throws()
    {
        var id = Guid.NewGuid();
        var r = new FileReceiver(_dst, id);
        r.Add(new FileEntry(id, "a.txt", false, 2));
        Assert.Throws<InvalidDataException>(() => r.Write(new byte[3]));
    }

    [Fact]
    public void Receiver_MissingBytes_CompleteThrows()
    {
        var id = Guid.NewGuid();
        var r = new FileReceiver(_dst, id);
        r.Add(new FileEntry(id, "a.txt", false, 5));
        r.Write(new byte[2]);
        Assert.Throws<InvalidDataException>(() => r.Complete());
    }

    [Fact]
    public void Receiver_Cancel_DeletesFolder()
    {
        var id = Guid.NewGuid();
        var r = new FileReceiver(_dst, id);
        r.Add(new FileEntry(id, "a.txt", false, 5));
        r.Write(new byte[2]);
        r.Cancel();
        Assert.False(Directory.Exists(Path.Combine(_dst, id.ToString("N"))));
    }

    [Fact]
    public void CleanupExcept_KeepsOnlyGiven()
    {
        var keep = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_dst, keep.ToString("N")));
        Directory.CreateDirectory(Path.Combine(_dst, Guid.NewGuid().ToString("N")));
        FileReceiver.CleanupExcept(_dst, keep);
        Assert.Equal(new[] { Path.Combine(_dst, keep.ToString("N")) }, Directory.GetDirectories(_dst));
        FileReceiver.CleanupExcept(_dst, null);
        Assert.Empty(Directory.GetDirectories(_dst));
    }
}
