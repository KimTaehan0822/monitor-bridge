using MonitorBridge.Core.Files;
using MonitorBridge.Core.Net;
using MonitorBridge.Core.Protocol;
using Message = MonitorBridge.Core.Protocol.Message;

namespace MonitorBridge.Shared;

/// <summary>
/// Simple file copy: a copied file list is pushed to the other PC's temp folder at once, then put on its clipboard.
/// Tracks one transfer at a time (the latest) so people can see how far it got.
/// </summary>
internal sealed class FileTransfer
{
    public const int ChunkSize = 60 * 1024; // fits one 64KB fragment with its header
    public static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "MonitorBridge");
    const long MiB = 1L << 20;
    const string DisconnectedNotice = "연결이 끊겨서 파일 전송이 멈췄어요. 다시 복사해 주세요.";

    readonly Func<long> _limitBytes;
    readonly string _peerName;
    readonly Action<string> _notify;
    readonly Func<string> _receiveFolder;
    readonly Action<IReadOnlyList<string>, string> _onSaved;
    readonly object _gate = new();
    CancellationTokenSource? _sending;
    Guid _sendingId;
    FileReceiver? _rx;
    Guid _rxId;
    long _rxRemaining;
    string _rxDest = "";
    readonly HashSet<string> _rxTops = new(StringComparer.OrdinalIgnoreCase);

    readonly TransferTracker _track; // guarded by _gate

    /// <summary>The peer's copy limit in MB, once it has told us (null = unknown).</summary>
    public int? PeerLimitMB { get; set; }

    /// <param name="onSaved">Saved items' final paths and the folder they went to; the caller tells the person.</param>
    public FileTransfer(Func<long> limitBytes, Func<string> receiveFolder, string peerName, Action<string> notify,
        Action<IReadOnlyList<string>, string> onSaved)
    {
        _limitBytes = limitBytes;
        _receiveFolder = receiveFolder;
        _peerName = peerName;
        _notify = notify;
        _onSaved = onSaved;
        _track = new TransferTracker(peerName);
        // Old builds received into %TEMP%; anything still there would never be found, so clear it once.
        FileReceiver.CleanupExcept(TempRoot, null);
        try { FileReceiver.CleanupExcept(Path.Combine(receiveFolder(), FileReceiver.StagingFolderName), null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Log.Error(e); }
    }

    public TransferProgress? Progress { get { lock (_gate) return _track.Current; } }

    static string NameOf(IReadOnlyList<string> paths)
    {
        var first = Path.GetFileName(paths[0].TrimEnd('\\', '/'));
        return paths.Count == 1 ? first : $"{first} 외 {paths.Count - 1}개";
    }

    public void Send(PeerConnection conn, IReadOnlyList<string> paths)
    {
        CancellationTokenSource cts;
        Guid id = Guid.NewGuid();
        lock (_gate)
        {
            if (_sending is not null) { _sending.Cancel(); conn.Send(new FileCancel(_sendingId)); }
            _sending = cts = new CancellationTokenSource();
            _sendingId = id;
        }
        _ = Task.Run(async () =>
        {
            string name = NameOf(paths);
            try
            {
                var (entries, total) = FileBundle.Plan(paths);
                if (entries.Count == 0) return;
                long own = _limitBytes();
                long? peer = PeerLimitMB is { } p ? p * MiB : null;
                if (total > TransferProgress.EffectiveLimit(own, peer))
                {
                    _notify(peer is { } pb && pb < own
                        ? $"상대 PC 한도({pb / MiB:N0}MB)보다 커서 보내지 않았어요. 상대 PC 설정에서 한도를 올려 주세요."
                        : $"파일이 {own / MiB:N0}MB를 넘어서 상대 PC로 보내지 않았어요.");
                    return;
                }
                lock (_gate)
                {
                    if (cts.IsCancellationRequested) return;
                    _track.Begin(true, id, name, total);
                }
                _notify($"{_peerName}으로 파일을 보내기 시작했어요: {name} ({TransferProgress.Size(total)})");
                await conn.SendBulkAsync(new FileOfferStart(id, total, entries.Count), cts.Token);
                foreach (var e in entries)
                    await conn.SendBulkAsync(new FileEntry(id, e.RelativePath.Replace('/', '\\'), e.IsDirectory, e.Length), cts.Token);
                var buf = new byte[ChunkSize];
                foreach (var e in entries.Where(e => !e.IsDirectory))
                {
                    long left = e.Length;
                    await using var fs = new FileStream(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    while (left > 0)
                    {
                        int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), cts.Token);
                        if (n == 0) throw new IOException($"{e.FullPath} 파일이 복사 도중 바뀌었어요");
                        await conn.SendBulkAsync(new FileChunk(id, buf[..n]), cts.Token);
                        lock (_gate) _track.Advance(true, id, n);
                        left -= n;
                    }
                }
                await conn.SendBulkAsync(new FileOfferEnd(id), cts.Token);
                bool done;
                lock (_gate) done = _track.Finish(true, id, TransferState.Done);
                if (done) _notify($"다 보냈어요: {name}");
            }
            catch (OperationCanceledException) { lock (_gate) _track.Finish(true, id, TransferState.Cancelled); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex);
                lock (_gate) _track.Finish(true, id, TransferState.Failed);
                try { conn.Send(new FileCancel(id)); } catch (Exception e2) { Log.Error(e2); }
                _notify("파일을 읽지 못해서 보내지 않았어요: " + ex.Message);
            }
            catch (Exception ex)
            {
                // Typically the connection closed mid-transfer; whichever path ends it first tells the person.
                Log.Error(ex);
                bool failed;
                lock (_gate) failed = _track.Finish(true, id, TransferState.Failed);
                if (failed) _notify(DisconnectedNotice);
            }
        });
    }

    /// <summary>The link dropped: nothing in flight can finish, so say so instead of showing a frozen percentage.</summary>
    public void OnDisconnected()
    {
        bool failed;
        lock (_gate)
        {
            _sending?.Cancel();
            _sending = null;
            _rx?.Cancel();
            _rx = null;
            failed = _track.FailAll();
        }
        if (failed) _notify(DisconnectedNotice);
    }

    /// <summary>Called on the network thread for every file message from the peer.</summary>
    public void OnMessage(Message m)
    {
        lock (_gate)
        {
            try
            {
                switch (m)
                {
                    case FileOfferStart s:
                        if (_rx is not null) _track.Finish(false, _rxId, TransferState.Cancelled);
                        _rx?.Cancel();
                        _rx = null;
                        // The peer is a trust boundary: enforce our own limit too.
                        if (s.TotalBytes < 0 || s.TotalBytes > _limitBytes())
                        {
                            _notify($"파일이 이 PC 한도({_limitBytes() / MiB:N0}MB)보다 커서 받지 않았어요. 설정에서 한도를 올려 주세요.");
                            return;
                        }
                        var dest = _receiveFolder();
                        string staging;
                        try
                        {
                            Directory.CreateDirectory(dest);
                            staging = Path.Combine(dest, FileReceiver.StagingFolderName);
                            Directory.CreateDirectory(staging).Attributes |= FileAttributes.Hidden;
                            FileReceiver.CleanupExcept(staging, null);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            Log.Error(ex);
                            _notify($"받은 파일 저장 폴더({dest})에 쓸 수 없어서 받지 않았어요. 설정에서 폴더를 바꿔 주세요.");
                            return;
                        }
                        _rx = new FileReceiver(staging, s.Id);
                        _rxDest = dest;
                        _rxId = s.Id;
                        _rxRemaining = s.TotalBytes;
                        _rxTops.Clear();
                        _track.Begin(false, s.Id, "", s.TotalBytes);
                        _notify($"{_peerName}에서 파일을 받기 시작했어요: {TransferProgress.Size(s.TotalBytes)}");
                        break;
                    case FileEntry e when _rx is not null && e.Id == _rxId:
                        _rx.Add(e);
                        var top = e.RelativePath.Split('\\', '/')[0];
                        if (_rxTops.Add(top))
                            _track.SetName(false, e.Id, _rxTops.Count == 1 ? top : $"{_rxTops.First()} 외 {_rxTops.Count - 1}개");
                        break;
                    case FileChunk c when _rx is not null && c.Id == _rxId:
                        _rxRemaining -= c.Data.Length;
                        if (_rxRemaining < 0) throw new InvalidDataException("More data than announced");
                        _rx.Write(c.Data);
                        _track.Advance(false, c.Id, c.Data.Length);
                        break;
                    case FileOfferEnd end when _rx is not null && end.Id == _rxId:
                        var tops = _rx.Complete();
                        var saved = FileReceiver.MoveInto(tops, _rxDest);
                        _rx.Cancel(); // only the emptied staging folder is left; remove it
                        _rx = null;
                        _track.Finish(false, end.Id, TransferState.Done);
                        _onSaved(saved, _rxDest);
                        break;
                    case FileCancel cancel when _rx is not null && cancel.Id == _rxId:
                        _rx.Cancel();
                        _rx = null;
                        _track.Finish(false, cancel.Id, TransferState.Cancelled);
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Log.Error(ex);
                if (_rx is not null) _track.Finish(false, _rxId, TransferState.Failed);
                _rx?.Cancel();
                _rx = null;
                _notify("상대 PC에서 온 파일을 받지 못했어요: " + ex.Message);
            }
        }
    }
}
