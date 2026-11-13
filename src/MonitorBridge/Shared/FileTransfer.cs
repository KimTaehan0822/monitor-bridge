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

    readonly Func<long> _limitBytes;
    readonly string _peerName;
    readonly Action<string> _notify;
    readonly Action<IReadOnlyList<string>> _onReceived;
    readonly object _gate = new();
    CancellationTokenSource? _sending;
    Guid _sendingId;
    FileReceiver? _rx;
    Guid _rxId;
    long _rxRemaining;
    readonly HashSet<string> _rxTops = new(StringComparer.OrdinalIgnoreCase);

    // Progress of the latest transfer, guarded by _gate.
    Guid _pId;
    bool _pSending;
    string _pName = "";
    long _pTotal, _pDone, _pStart, _pEnd;
    TransferState _pState;
    bool _pActive;

    /// <summary>The peer's copy limit in MB, once it has told us (null = unknown).</summary>
    public int? PeerLimitMB { get; set; }

    public FileTransfer(Func<long> limitBytes, string peerName, Action<string> notify, Action<IReadOnlyList<string>> onReceived)
    {
        _limitBytes = limitBytes;
        _peerName = peerName;
        _notify = notify;
        _onReceived = onReceived;
        FileReceiver.CleanupExcept(TempRoot, null);
    }

    public TransferProgress? Progress
    {
        get
        {
            lock (_gate)
            {
                if (!_pActive) return null;
                long end = _pState == TransferState.Running ? Environment.TickCount64 : _pEnd;
                return new TransferProgress(_pSending, _peerName, _pName, _pTotal, _pDone, end - _pStart, _pState);
            }
        }
    }

    /// <summary>Caller holds _gate. A transfer still running is replaced, so it shows as cancelled, never stuck.</summary>
    void Begin(Guid id, bool sending, string name, long total)
    {
        _pId = id;
        _pSending = sending;
        _pName = name;
        _pTotal = total;
        _pDone = 0;
        _pStart = Environment.TickCount64;
        _pState = TransferState.Running;
        _pActive = true;
    }

    /// <summary>Caller holds _gate. Only the transfer still shown and still running can finish.</summary>
    bool Finish(Guid id, TransferState state)
    {
        if (!_pActive || _pId != id || _pState != TransferState.Running) return false;
        _pState = state;
        _pEnd = Environment.TickCount64;
        return true;
    }

    void Advance(Guid id, long bytes)
    {
        lock (_gate) if (_pId == id && _pState == TransferState.Running) _pDone += bytes;
    }

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
                    if (_pActive) Finish(_pId, TransferState.Cancelled);
                    Begin(id, true, name, total);
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
                        Advance(id, n);
                        left -= n;
                    }
                }
                await conn.SendBulkAsync(new FileOfferEnd(id), cts.Token);
                bool done;
                lock (_gate) done = Finish(id, TransferState.Done);
                if (done) _notify($"다 보냈어요: {name}");
            }
            catch (OperationCanceledException) { lock (_gate) Finish(id, TransferState.Cancelled); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex);
                lock (_gate) Finish(id, TransferState.Failed);
                try { conn.Send(new FileCancel(id)); } catch (Exception e2) { Log.Error(e2); }
                _notify("파일을 읽지 못해서 보내지 않았어요: " + ex.Message);
            }
            catch (Exception ex)
            {
                // Typically the connection closed mid-transfer.
                Log.Error(ex);
                lock (_gate) Finish(id, TransferState.Failed);
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
            failed = _pActive && Finish(_pId, TransferState.Failed);
        }
        if (failed) _notify("연결이 끊겨서 파일 전송이 멈췄어요. 다시 복사해 주세요.");
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
                        _rx?.Cancel();
                        _rx = null;
                        if (_pActive && !_pSending) Finish(_pId, TransferState.Cancelled);
                        FileReceiver.CleanupExcept(TempRoot, null);
                        // The peer is a trust boundary: enforce our own limit too.
                        if (s.TotalBytes < 0 || s.TotalBytes > _limitBytes())
                        {
                            _notify($"파일이 이 PC 한도({_limitBytes() / MiB:N0}MB)보다 커서 받지 않았어요. 설정에서 한도를 올려 주세요.");
                            return;
                        }
                        _rx = new FileReceiver(TempRoot, s.Id);
                        _rxId = s.Id;
                        _rxRemaining = s.TotalBytes;
                        _rxTops.Clear();
                        Begin(s.Id, false, "", s.TotalBytes);
                        _notify($"{_peerName}에서 파일을 받기 시작했어요: {TransferProgress.Size(s.TotalBytes)}");
                        break;
                    case FileEntry e when _rx is not null && e.Id == _rxId:
                        _rx.Add(e);
                        var top = e.RelativePath.Split('\\', '/')[0];
                        if (_rxTops.Add(top) && _pId == e.Id)
                            _pName = _rxTops.Count == 1 ? top : $"{_rxTops.First()} 외 {_rxTops.Count - 1}개";
                        break;
                    case FileChunk c when _rx is not null && c.Id == _rxId:
                        _rxRemaining -= c.Data.Length;
                        if (_rxRemaining < 0) throw new InvalidDataException("More data than announced");
                        _rx.Write(c.Data);
                        if (_pId == c.Id) _pDone += c.Data.Length;
                        break;
                    case FileOfferEnd end when _rx is not null && end.Id == _rxId:
                        var tops = _rx.Complete();
                        _rx = null;
                        Finish(end.Id, TransferState.Done);
                        _onReceived(tops);
                        _notify("다 받았어요. 붙여넣으세요(Ctrl+V).");
                        break;
                    case FileCancel cancel when _rx is not null && cancel.Id == _rxId:
                        _rx.Cancel();
                        _rx = null;
                        Finish(cancel.Id, TransferState.Cancelled);
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Log.Error(ex);
                _rx?.Cancel();
                _rx = null;
                if (_pActive && !_pSending) Finish(_pId, TransferState.Failed);
                _notify("상대 PC에서 온 파일을 받지 못했어요: " + ex.Message);
            }
        }
    }
}
