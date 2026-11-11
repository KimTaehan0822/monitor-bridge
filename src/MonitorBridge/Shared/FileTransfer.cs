using MonitorBridge.Core.Files;
using MonitorBridge.Core.Net;
using MonitorBridge.Core.Protocol;
using Message = MonitorBridge.Core.Protocol.Message;

namespace MonitorBridge.Shared;

/// <summary>
/// Simple file copy: a copied file list is pushed to the other PC's temp folder at once, then put on its clipboard.
/// </summary>
internal sealed class FileTransfer
{
    public const int ChunkSize = 60 * 1024; // fits one 64KB fragment with its header
    public static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "MonitorBridge");

    readonly Func<long> _limitBytes;
    readonly Action<string> _notify;
    readonly Action<IReadOnlyList<string>> _onReceived;
    readonly object _gate = new();
    CancellationTokenSource? _sending;
    Guid _sendingId;
    FileReceiver? _rx;
    Guid _rxId;
    long _rxRemaining;

    public FileTransfer(Func<long> limitBytes, Action<string> notify, Action<IReadOnlyList<string>> onReceived)
    {
        _limitBytes = limitBytes;
        _notify = notify;
        _onReceived = onReceived;
        FileReceiver.CleanupExcept(TempRoot, null);
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
            try
            {
                var (entries, total) = FileBundle.Plan(paths);
                if (entries.Count == 0) return;
                if (total > _limitBytes())
                {
                    _notify($"파일이 {_limitBytes() / (1024 * 1024)}MB를 넘어서 상대 PC로 보내지 않았어요.");
                    return;
                }
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
                        left -= n;
                    }
                }
                await conn.SendBulkAsync(new FileOfferEnd(id), cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex);
                conn.Send(new FileCancel(id));
                _notify("파일을 읽지 못해서 보내지 않았어요: " + ex.Message);
            }
            catch (Exception ex) { Log.Error(ex); }
        });
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
                        FileReceiver.CleanupExcept(TempRoot, null);
                        // The peer is a trust boundary: enforce our own limit too.
                        if (s.TotalBytes < 0 || s.TotalBytes > _limitBytes()) return;
                        _rx = new FileReceiver(TempRoot, s.Id);
                        _rxId = s.Id;
                        _rxRemaining = s.TotalBytes;
                        break;
                    case FileEntry e when _rx is not null && e.Id == _rxId:
                        _rx.Add(e);
                        break;
                    case FileChunk c when _rx is not null && c.Id == _rxId:
                        _rxRemaining -= c.Data.Length;
                        if (_rxRemaining < 0) throw new InvalidDataException("More data than announced");
                        _rx.Write(c.Data);
                        break;
                    case FileOfferEnd end when _rx is not null && end.Id == _rxId:
                        var top = _rx.Complete();
                        _rx = null;
                        _onReceived(top);
                        break;
                    case FileCancel cancel when _rx is not null && cancel.Id == _rxId:
                        _rx.Cancel();
                        _rx = null;
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Log.Error(ex);
                _rx?.Cancel();
                _rx = null;
                _notify("상대 PC에서 온 파일을 받지 못했어요: " + ex.Message);
            }
        }
    }
}
