using MonitorBridge.Core.Files;
using MonitorBridge.Core.Protocol;
using MonitorBridge.Shared;
using Message = MonitorBridge.Core.Protocol.Message;

namespace MonitorBridge.Core.Tests;

/// <summary>The receiving side of FileTransfer, driven message by message (no network).</summary>
public class FileTransferTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-ft-" + Guid.NewGuid().ToString("N"));
    readonly List<Message> _replies = new();
    readonly List<string> _notices = new();
    IReadOnlyList<string>? _saved;

    public FileTransferTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    FileTransfer New(string folder, long limit = 1L << 30) =>
        new(() => limit, () => folder, "게임컴", _notices.Add, (paths, _) => _saved = paths, _replies.Add,
            legacyTempRoot: Path.Combine(_dir, "no-legacy"));

    void Send(FileTransfer t, Guid id, string name, byte[] data, bool end = true)
    {
        t.OnMessage(new FileOfferStart(id, data.Length, 1));
        t.OnMessage(new FileEntry(id, name, false, data.Length));
        t.OnMessage(new FileChunk(id, data));
        if (end) t.OnMessage(new FileOfferEnd(id));
    }

    [Fact]
    public void UnwritableFolder_RejectsAndTellsSender()
    {
        var file = Path.Combine(_dir, "afile");
        File.WriteAllText(file, "x");
        var t = New(Path.Combine(file, "sub")); // a folder cannot be made under a file
        var id = Guid.NewGuid();
        t.OnMessage(new FileOfferStart(id, 10, 1));
        Assert.Equal(new FileCancel(id), Assert.Single(_replies));
        Assert.Contains(_notices, n => n.Contains("쓸 수 없어서"));
    }

    [Fact]
    public void OverLimit_RejectsAndTellsSender()
    {
        var t = New(_dir, limit: 5);
        var id = Guid.NewGuid();
        t.OnMessage(new FileOfferStart(id, 10, 1));
        Assert.Equal(new FileCancel(id), Assert.Single(_replies));
    }

    [Fact]
    public void Complete_SavesIntoFolder_AndLeavesNoStaging()
    {
        var t = New(_dir);
        Send(t, Guid.NewGuid(), "a.txt", new byte[] { 1, 2, 3 });
        Assert.Equal(new[] { Path.Combine(_dir, "a.txt") }, _saved);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_dir, "a.txt")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, FileReceiver.StagingFolderName)));
        Assert.Equal(TransferState.Done, t.Progress!.State);
    }

    [Fact]
    public void MoveFails_KeepsFilesAndSaysWhere()
    {
        var t = New(_dir);
        var id = Guid.NewGuid();
        Send(t, id, "a.txt", new byte[] { 1, 2, 3 }, end: false);
        var staged = Path.Combine(_dir, FileReceiver.StagingFolderName, id.ToString("N"), "a.txt");
        using (new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None)) // a scanner holding it
            t.OnMessage(new FileOfferEnd(id));
        Assert.True(File.Exists(staged));                                   // nothing deleted
        Assert.Contains(_notices, n => n.Contains(Path.GetDirectoryName(staged)!));
        Assert.Null(_saved);
        // A later transfer must not clean the kept files away.
        Send(t, Guid.NewGuid(), "b.txt", new byte[] { 9 });
        Assert.True(File.Exists(staged));
    }
}
