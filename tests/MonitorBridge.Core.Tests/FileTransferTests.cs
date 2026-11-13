using MonitorBridge.Core.Files;
using MonitorBridge.Core.Protocol;
using MonitorBridge.Shared;
using Message = MonitorBridge.Core.Protocol.Message;

namespace MonitorBridge.Core.Tests;

/// <summary>FileTransfer driven message by message; its network output is captured instead of sent.</summary>
public class FileTransferTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-ft-" + Guid.NewGuid().ToString("N"));
    readonly List<Message> _replies = new();
    readonly List<Message> _bulk = new();
    readonly List<string> _notices = new();
    readonly object _lock = new();
    IReadOnlyList<string>? _saved;
    SemaphoreSlim? _chunkGate; // when set, the 2nd chunk waits on it
    readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FileTransferTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    FileTransfer New(long limit = 1L << 30) =>
        new(() => limit, "게임컴", n => { lock (_lock) _notices.Add(n); }, (paths, _) => _saved = paths,
            m => { lock (_lock) _replies.Add(m); }, SendBulk, legacyTempRoot: Path.Combine(_dir, "no-legacy"));

    async ValueTask SendBulk(Message m, CancellationToken ct)
    {
        int chunks;
        lock (_lock) { _bulk.Add(m); chunks = _bulk.Count(x => x is FileChunk); }
        if (m is FileChunk && chunks == 2 && _chunkGate is not null) await _chunkGate.WaitAsync(ct);
        if (m is FileOfferEnd) _ended.TrySetResult();
    }

    T WaitFor<T>(List<Message> list) where T : Message
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            lock (_lock) if (list.OfType<T>().FirstOrDefault() is { } m) return m;
            Thread.Sleep(10);
        }
        throw new TimeoutException(typeof(T).Name);
    }

    string Folder(string name) { var p = Path.Combine(_dir, name); Directory.CreateDirectory(p); return p; }
    string Source(string name, int bytes)
    {
        var p = Path.Combine(Folder("src"), name);
        File.WriteAllBytes(p, Enumerable.Range(0, bytes).Select(i => (byte)i).ToArray());
        return p;
    }

    /// <summary>Receiver side: the peer offered one file and we asked for it.</summary>
    (FileTransfer T, Guid Id) Requested(string folder, long size, long limit = 1L << 30)
    {
        var t = New(limit);
        var id = Guid.NewGuid();
        t.OnMessage(new FileListOffer(id, 1, size, "a.txt"));
        Assert.Equal(RequestResult.Started, t.Request(folder));
        return (t, id);
    }

    void Stream(FileTransfer t, Guid id, string name, byte[] data, bool end = true)
    {
        t.OnMessage(new FileOfferStart(id, data.Length, 1));
        t.OnMessage(new FileEntry(id, name, false, data.Length));
        t.OnMessage(new FileChunk(id, data));
        if (end) t.OnMessage(new FileOfferEnd(id));
    }

    // ---- sender ----

    [Fact]
    public void CopyOnlyAnnounces()
    {
        var t = New();
        t.Offer(new[] { Source("a.txt", 10) });
        var offer = WaitFor<FileListOffer>(_replies);
        Assert.Equal((1, 10L, "a.txt"), (offer.Count, offer.TotalBytes, offer.FirstName));
        Thread.Sleep(50);
        lock (_lock) Assert.Empty(_bulk);
    }

    [Fact]
    public void NonFileCopyWithdraws()
    {
        New().Withdraw();
        Assert.Equal(new FileListOffer(Guid.Empty, 0, 0, ""), Assert.Single(_replies));
    }

    [Fact]
    public async Task RequestStreamsTheOfferedFiles()
    {
        var t = New();
        t.Offer(new[] { Source("a.txt", 10) });
        var offer = WaitFor<FileListOffer>(_replies);
        t.OnMessage(new FileRequest(offer.Id));
        await _ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (_lock)
        {
            Assert.IsType<FileOfferStart>(_bulk[0]);
            Assert.IsType<FileEntry>(_bulk[1]);
            Assert.Equal(10, _bulk.OfType<FileChunk>().Sum(c => c.Data.Length));
            Assert.IsType<FileOfferEnd>(_bulk[^1]);
            Assert.All(_bulk, m => Assert.Contains(offer.Id.ToString(), m.ToString()));
        }
    }

    [Fact]
    public void UnknownRequest_Refused()
    {
        var t = New();
        var other = Guid.NewGuid();
        t.OnMessage(new FileRequest(other));
        Assert.Equal(new FileCancel(other), Assert.Single(_replies));
    }

    [Fact]
    public void SourceGoneBeforeRequest_Refuses()
    {
        var t = New();
        var src = Source("a.txt", 10);
        t.Offer(new[] { src });
        var offer = WaitFor<FileListOffer>(_replies);
        File.Delete(src);
        t.OnMessage(new FileRequest(offer.Id));
        Assert.Equal(new FileCancel(offer.Id), WaitFor<FileCancel>(_replies));
        Thread.Sleep(50);
        lock (_lock) Assert.Single(_notices);
    }

    [Fact]
    public void CancelWhileSending_SourceUntouched()
    {
        var src = Source("big.bin", FileTransfer.ChunkSize * 3);
        var before = (File.ReadAllBytes(src), File.GetLastWriteTimeUtc(src));
        _chunkGate = new SemaphoreSlim(0);
        var t = New();
        t.Offer(new[] { src });
        var offer = WaitFor<FileListOffer>(_replies);
        t.OnMessage(new FileRequest(offer.Id));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until) { lock (_lock) if (_bulk.Count(m => m is FileChunk) >= 2) break; Thread.Sleep(10); }
        Assert.True(t.CancelAll());
        lock (_lock) Assert.Contains(new FileCancel(offer.Id), _replies);
        Assert.Equal(before.Item1, File.ReadAllBytes(src));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(src));
    }

    // ---- receiver ----

    [Fact]
    public void RequestWithoutOffer_ReturnsNothingOffered() =>
        Assert.Equal(RequestResult.NothingOffered, New().Request(Folder("dest")));

    [Fact]
    public void Withdrawn_ClearsPending()
    {
        var t = New();
        t.OnMessage(new FileListOffer(Guid.NewGuid(), 1, 3, "a.txt"));
        t.OnMessage(new FileListOffer(Guid.Empty, 0, 0, ""));
        Assert.Null(t.Pending);
    }

    [Fact]
    public void Request_OverLimit_IsTooBig()
    {
        var t = New(limit: 5);
        t.OnMessage(new FileListOffer(Guid.NewGuid(), 1, 10, "a.txt"));
        Assert.Equal(RequestResult.TooBig, t.Request(Folder("dest")));
        Assert.Empty(_replies);
    }

    [Fact]
    public void Request_SendsFileRequest_AndSavesIntoThatFolder()
    {
        var dest = Folder("dest");
        var (t, id) = Requested(dest, 3);
        Assert.Equal(new FileRequest(id), Assert.Single(_replies));
        Stream(t, id, "a.txt", new byte[] { 1, 2, 3 });
        Assert.Equal(new[] { Path.Combine(dest, "a.txt") }, _saved);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(dest, "a.txt")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(dest, FileReceiver.StagingFolderName)));
        Assert.Equal(TransferState.Done, t.Progress!.State);
    }

    [Fact]
    public void SecondRequestWhileReceiving_IsRefused()
    {
        var dest = Folder("dest");
        var (t, id) = Requested(dest, 6);
        t.OnMessage(new FileOfferStart(id, 6, 1));
        t.OnMessage(new FileEntry(id, "a.txt", false, 6));
        t.OnMessage(new FileChunk(id, new byte[3]));
        t.OnMessage(new FileListOffer(Guid.NewGuid(), 1, 1, "b.txt"));
        Assert.Equal(RequestResult.Busy, t.Request(dest));
        t.OnMessage(new FileChunk(id, new byte[3]));
        t.OnMessage(new FileOfferEnd(id));
        Assert.True(File.Exists(Path.Combine(dest, "a.txt"))); // the first one still finished
    }

    [Fact]
    public void ReceiverCancel_DeletesPartialAndTellsSender()
    {
        var dest = Folder("dest");
        var (t, id) = Requested(dest, 100);
        Stream(t, id, "a.txt", new byte[40], end: false);
        _replies.Clear();
        Assert.True(t.CancelAll());
        Assert.Equal(new FileCancel(id), Assert.Single(_replies));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dest, FileReceiver.StagingFolderName)));
        Assert.False(File.Exists(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public void SenderCancel_ReceiverCleansPartial()
    {
        var dest = Folder("dest");
        var (t, id) = Requested(dest, 100);
        Stream(t, id, "a.txt", new byte[40], end: false);
        t.OnMessage(new FileCancel(id));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dest, FileReceiver.StagingFolderName)));
        Assert.Equal(TransferState.Cancelled, t.Progress!.State);
    }

    [Fact]
    public void UnrequestedOfferStart_Refused()
    {
        var dest = Folder("dest");
        var t = New();
        var id = Guid.NewGuid();
        t.OnMessage(new FileOfferStart(id, 3, 1));
        Assert.Equal(new FileCancel(id), Assert.Single(_replies));
        Assert.False(Directory.Exists(Path.Combine(dest, FileReceiver.StagingFolderName)));
    }

    [Fact]
    public void UnwritableFolder_RejectsAndTellsSender()
    {
        var file = Path.Combine(_dir, "afile");
        File.WriteAllText(file, "x");
        var (t, id) = Requested(Path.Combine(file, "sub"), 10); // a folder cannot be made under a file
        _replies.Clear();
        t.OnMessage(new FileOfferStart(id, 10, 1));
        Assert.Equal(new FileCancel(id), Assert.Single(_replies));
        Assert.Contains(_notices, n => n.Contains("쓸 수 없어서"));
    }

    [Fact]
    public void MoveFails_KeepsFilesAndSaysWhere()
    {
        var dest = Folder("dest");
        var (t, id) = Requested(dest, 3);
        Stream(t, id, "a.txt", new byte[] { 1, 2, 3 }, end: false);
        var staged = Path.Combine(dest, FileReceiver.StagingFolderName, id.ToString("N"), "a.txt");
        using (new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None)) // a scanner holding it
            t.OnMessage(new FileOfferEnd(id));
        Assert.True(File.Exists(staged));                                   // nothing deleted
        Assert.Contains(_notices, n => n.Contains(Path.GetDirectoryName(staged)!));
        Assert.Null(_saved);
    }
}
