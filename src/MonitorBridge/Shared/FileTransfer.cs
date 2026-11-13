using MonitorBridge.Core.Files;
using MonitorBridge.Core.Protocol;
using Message = MonitorBridge.Core.Protocol.Message;

namespace MonitorBridge.Shared;

internal enum RequestResult { Started, NothingOffered, Busy, TooBig }

/// <summary>
/// Copy announces, paste fetches: copying files only tells the peer what is on offer; the files travel when the
/// peer asks for them (right-click paste) and land in the folder it chose. Source files are only ever read.
/// </summary>
internal sealed class FileTransfer
{
    public const int ChunkSize = 60 * 1024; // fits one 64KB fragment with its header
    public static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "MonitorBridge");
    const long MiB = 1L << 20;
    const string DisconnectedNotice = "연결이 끊겨서 파일 전송이 멈췄어요. 다시 가져와 주세요.";

    readonly Func<long> _limitBytes;
    readonly string _peerName;
    readonly Action<string> _notify;
    readonly Action<IReadOnlyList<string>, string> _onSaved;
    readonly Action<Message> _reply;
    readonly Func<Message, CancellationToken, ValueTask> _sendBulk;
    readonly object _gate = new();
    readonly TransferTracker _track; // guarded by _gate

    // Sender side.
    int _offerSeq;
    Guid _offerId;
    IReadOnlyList<string>? _offerPaths;
    CancellationTokenSource? _sending;
    Guid _sendingId;

    // Receiver side.
    FileListOffer? _pending;
    Guid _requestedId;
    string _requestedFolder = "";
    FileReceiver? _rx;
    Guid _rxId;
    long _rxRemaining;
    string _rxDest = "";
    readonly HashSet<string> _rxTops = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The peer's copy limit in MB, once it has told us (null = unknown).</summary>
    public int? PeerLimitMB { get; set; }

    /// <param name="onSaved">Saved items' final paths and their folder; the caller tells the person.</param>
    /// <param name="reply">Sends a small message to the peer right away.</param>
    /// <param name="sendBulk">Queues a file message behind cursor traffic.</param>
    public FileTransfer(Func<long> limitBytes, string peerName, Action<string> notify,
        Action<IReadOnlyList<string>, string> onSaved, Action<Message> reply,
        Func<Message, CancellationToken, ValueTask> sendBulk, string? legacyTempRoot = null)
    {
        _limitBytes = limitBytes;
        _peerName = peerName;
        _notify = notify;
        _onSaved = onSaved;
        _reply = reply;
        _sendBulk = sendBulk;
        _track = new TransferTracker(peerName);
        // Old builds received into %TEMP%; anything still there would never be found, so clear it once.
        FileReceiver.CleanupExcept(legacyTempRoot ?? TempRoot, null);
    }

    public TransferProgress? Progress { get { lock (_gate) return _track.Current; } }

    /// <summary>What the peer has copied and we could fetch, or null.</summary>
    public FileListOffer? Pending { get { lock (_gate) return _pending; } }

    static string NameOf(IReadOnlyList<string> paths)
    {
        var first = Path.GetFileName(paths[0].TrimEnd('\\', '/'));
        return paths.Count == 1 ? first : $"{first} 외 {paths.Count - 1}개";
    }

    // ---------------- sender ----------------

    /// <summary>Files were copied here: tell the peer what is on offer (names and size only).</summary>
    public void Offer(IReadOnlyList<string> paths)
    {
        int seq;
        lock (_gate) seq = ++_offerSeq;
        var copy = paths.ToArray();
        // Sizing a big folder walks the disk; keep it off the clipboard (UI) thread.
        _ = Task.Run(() =>
        {
            try
            {
                var (entries, total) = FileBundle.Plan(copy);
                var id = Guid.NewGuid();
                lock (_gate)
                {
                    if (seq != _offerSeq) return; // a newer copy won
                    _offerId = entries.Count > 0 ? id : Guid.Empty;
                    _offerPaths = entries.Count > 0 ? copy : null;
                }
                _reply(entries.Count > 0 ? new FileListOffer(id, copy.Length, total, NameOf(copy)) : new FileListOffer(Guid.Empty, 0, 0, ""));
            }
            catch (Exception e) { Log.Error(e); }
        });
    }

    /// <summary>Something other than files was copied: nothing is on offer any more.</summary>
    public void Withdraw()
    {
        lock (_gate)
        {
            _offerSeq++;
            _offerId = Guid.Empty;
            _offerPaths = null;
        }
        _reply(new FileListOffer(Guid.Empty, 0, 0, ""));
    }

    /// <summary>After a reconnect the peer has forgotten our offer; tell it again.</summary>
    public void Reannounce()
    {
        IReadOnlyList<string>? paths;
        lock (_gate) paths = _offerPaths;
        if (paths is not null) Offer(paths);
    }

    void StartSend(Guid id, IReadOnlyList<string> paths)
    {
        var cts = new CancellationTokenSource();
        _sending = cts;
        _sendingId = id;
        _ = Task.Run(async () =>
        {
            string name = NameOf(paths);
            try
            {
                // Re-read now: the files may have changed or gone since they were copied.
                var (entries, total) = FileBundle.Plan(paths);
                if (entries.Count == 0)
                {
                    Refuse(id, cts, "복사해 둔 파일을 찾을 수 없어서 보내지 않았어요. 파일을 다시 복사해 주세요.");
                    return;
                }
                long own = _limitBytes();
                long? peer = PeerLimitMB is { } p ? p * MiB : null;
                if (total > TransferProgress.EffectiveLimit(own, peer))
                {
                    Refuse(id, cts, peer is { } pb && pb < own
                        ? $"상대 PC 한도({pb / MiB:N0}MB)보다 커서 보내지 않았어요. 상대 PC 설정에서 한도를 올려 주세요."
                        : $"파일이 이 PC 한도({own / MiB:N0}MB)를 넘어서 보내지 않았어요.");
                    return;
                }
                lock (_gate)
                {
                    if (cts.IsCancellationRequested) return;
                    _track.Begin(true, id, name, total);
                }
                _notify($"{_peerName}으로 파일을 보내기 시작했어요: {name} ({TransferProgress.Size(total)})");
                await _sendBulk(new FileOfferStart(id, total, entries.Count), cts.Token);
                foreach (var e in entries)
                    await _sendBulk(new FileEntry(id, e.RelativePath.Replace('/', '\\'), e.IsDirectory, e.Length), cts.Token);
                var buf = new byte[ChunkSize];
                foreach (var e in entries.Where(e => !e.IsDirectory))
                {
                    long left = e.Length;
                    // Read-only, and other programs may keep using the file: the source is never changed.
                    await using var fs = new FileStream(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    while (left > 0)
                    {
                        int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), cts.Token);
                        if (n == 0) throw new IOException($"{e.FullPath} 파일이 보내는 도중 바뀌었어요");
                        await _sendBulk(new FileChunk(id, buf[..n]), cts.Token);
                        lock (_gate) _track.Advance(true, id, n);
                        left -= n;
                    }
                }
                await _sendBulk(new FileOfferEnd(id), cts.Token);
                bool done;
                lock (_gate)
                {
                    done = _track.Finish(true, id, TransferState.Done);
                    if (ReferenceEquals(_sending, cts)) _sending = null;
                }
                if (done) _notify($"다 보냈어요: {name}");
            }
            catch (OperationCanceledException) { lock (_gate) _track.Finish(true, id, TransferState.Cancelled); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex);
                Refuse(id, cts, "파일을 읽지 못해서 보내지 않았어요: " + ex.Message);
            }
            catch (Exception ex)
            {
                // Typically the connection closed mid-transfer; whichever path ends it first tells the person.
                Log.Error(ex);
                bool failed;
                lock (_gate)
                {
                    failed = _track.Finish(true, id, TransferState.Failed);
                    if (ReferenceEquals(_sending, cts)) _sending = null;
                }
                if (failed) _notify(DisconnectedNotice);
            }
        });
    }

    /// <summary>Sender gives up on a request: tell the peer so it stops waiting, and tell the person why.</summary>
    void Refuse(Guid id, CancellationTokenSource cts, string why)
    {
        lock (_gate)
        {
            _track.Finish(true, id, TransferState.Failed);
            if (ReferenceEquals(_sending, cts)) _sending = null;
        }
        _reply(new FileCancel(id));
        _notify(why);
    }

    // ---------------- receiver ----------------

    /// <summary>Right-click paste: ask the peer for what it offered, to be saved into <paramref name="folder"/>.</summary>
    public RequestResult Request(string folder)
    {
        Guid id;
        lock (_gate)
        {
            if (_pending is null) return RequestResult.NothingOffered;
            if (_rx is not null || _requestedId != Guid.Empty) return RequestResult.Busy;
            long? peer = PeerLimitMB is { } p ? p * MiB : null;
            if (_pending.TotalBytes > TransferProgress.EffectiveLimit(_limitBytes(), peer)) return RequestResult.TooBig;
            id = _requestedId = _pending.Id;
            _requestedFolder = folder;
        }
        _reply(new FileRequest(id));
        return RequestResult.Started;
    }

    /// <summary>Stops every transfer in flight on this PC, tells the peer, and deletes half-received files.</summary>
    public bool CancelAll()
    {
        bool sent = false, received = false;
        lock (_gate)
        {
            if (_sending is not null)
            {
                _sending.Cancel();
                _track.Finish(true, _sendingId, TransferState.Cancelled);
                _reply(new FileCancel(_sendingId));
                _sending = null;
                sent = true;
            }
            if (_rx is not null)
            {
                _rx.Cancel(); // only the hidden staging copy; nothing outside it
                _track.Finish(false, _rxId, TransferState.Cancelled);
                _reply(new FileCancel(_rxId));
                _rx = null;
                received = true;
            }
            if (_requestedId != Guid.Empty)
            {
                _reply(new FileCancel(_requestedId));
                _requestedId = Guid.Empty;
                received = true;
            }
        }
        if (received) _notify("파일 전송을 취소했어요. 받던 파일은 지웠어요.");
        else if (sent) _notify("파일 전송을 취소했어요.");
        return sent || received;
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
            _pending = null;
            _requestedId = Guid.Empty;
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
                    case FileListOffer o:
                        _pending = o.Count > 0 ? o : null;
                        break;
                    case FileRequest r:
                        if (r.Id != _offerId || _offerPaths is null || _sending is not null) { _reply(new FileCancel(r.Id)); break; }
                        StartSend(r.Id, _offerPaths);
                        break;
                    case FileOfferStart s:
                        // Files only come when asked for: anything unrequested is refused, nothing is written.
                        if (s.Id != _requestedId || _rx is not null) { _reply(new FileCancel(s.Id)); return; }
                        _requestedId = Guid.Empty;
                        if (s.TotalBytes < 0 || s.TotalBytes > _limitBytes())
                        {
                            _notify($"파일이 이 PC 한도({_limitBytes() / MiB:N0}MB)보다 커서 받지 않았어요. 설정에서 한도를 올려 주세요.");
                            _reply(new FileCancel(s.Id));
                            return;
                        }
                        var dest = _requestedFolder;
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
                            _notify($"이 폴더({dest})에 쓸 수 없어서 받지 않았어요. 다른 폴더에서 다시 가져와 주세요.");
                            _reply(new FileCancel(s.Id));
                            return;
                        }
                        _rx = new FileReceiver(staging, s.Id);
                        _rxDest = dest;
                        _rxId = s.Id;
                        _rxRemaining = s.TotalBytes;
                        _rxTops.Clear();
                        _track.Begin(false, s.Id, _pending?.Id == s.Id ? _pending.FirstName : "", s.TotalBytes);
                        _notify($"{_peerName}에서 파일을 받기 시작했어요: {TransferProgress.Size(s.TotalBytes)}. 트레이 아이콘을 누르면 진행 상황이 보여요.");
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
                        var saved = FileReceiver.MoveInto(tops, _rxDest, out var notMoved);
                        if (notMoved.Count > 0)
                        {
                            // Never delete what arrived: keep it, protect it from cleanup and say where it is.
                            _rx.KeepForUser();
                            _track.Finish(false, end.Id, TransferState.Failed);
                            _notify($"받은 파일을 폴더로 옮기지 못했어요. 파일은 {_rx.Root} 폴더에 그대로 있어요. 직접 옮겨 주세요.");
                            _rx = null;
                            break;
                        }
                        _rx.Cancel(); // only the emptied staging folder is left; remove it
                        _rx = null;
                        _track.Finish(false, end.Id, TransferState.Done);
                        _onSaved(saved, _rxDest);
                        break;
                    case FileCancel c when _sending is not null && c.Id == _sendingId:
                        // The peer stopped it (its cancel button, or it refused); it shows its own reason.
                        if (_track.Finish(true, c.Id, TransferState.Failed)) _notify("상대 PC에서 파일 전송을 멈췄어요.");
                        _sending.Cancel();
                        _sending = null;
                        break;
                    case FileCancel c when _rx is not null && c.Id == _rxId:
                        _rx.Cancel();
                        _rx = null;
                        _track.Finish(false, c.Id, TransferState.Cancelled);
                        _notify("상대 PC에서 파일 전송을 멈췄어요. 받던 파일은 지웠어요.");
                        break;
                    case FileCancel c when c.Id == _requestedId && _requestedId != Guid.Empty:
                        _requestedId = Guid.Empty; // the sender could not send; its PC shows why
                        _notify("상대 PC가 파일을 보내지 못했어요. 상대 PC에 뜬 알림을 확인해 주세요.");
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Log.Error(ex);
                if (_rx is not null)
                {
                    _track.Finish(false, _rxId, TransferState.Failed);
                    _reply(new FileCancel(_rxId));
                }
                _rx?.Cancel();
                _rx = null;
                _notify("상대 PC에서 온 파일을 받지 못했어요: " + ex.Message);
            }
        }
    }
}
