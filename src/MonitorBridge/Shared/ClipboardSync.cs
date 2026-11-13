using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using MonitorBridge.Core.Clipboard;
using MonitorBridge.Core.Net;
using MonitorBridge.Core.Protocol;
using MonitorBridge.Native;
using Message = MonitorBridge.Core.Protocol.Message;

namespace MonitorBridge.Shared;

/// <summary>
/// Two-way clipboard sync (text, image, files). Lives on the UI (STA) thread; the clipboard is only touched there.
/// </summary>
internal sealed class ClipboardSync : NativeWindow, IDisposable
{
    readonly Func<PeerConnection?> _conn;
    readonly SynchronizationContext _ui;
    readonly ClipboardEcho _echo = new();
    readonly FileTransfer _files;
    // Disk work must not run on the network read loop that also carries cursor moves.
    // shortcut: unbounded, relies on disk being faster than the LAN; bound it if slow disks show memory growth.
    readonly Channel<Message> _fileQueue = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });

    readonly Func<long> _fileLimitBytes;
    readonly Func<int> _speedMBps;

    /// <summary>Files fetched from the peer were saved (final paths, folder). Raised on the UI thread.</summary>
    public event Action<IReadOnlyList<string>, string>? Saved;
    /// <summary>The peer said which build it runs (network thread).</summary>
    public event Action<string>? PeerBuild;

    /// <param name="peerName">"게임컴" or "송출컴": the other PC, as people call it in notices.</param>
    public ClipboardSync(Func<PeerConnection?> conn, Func<long> fileLimitBytes, Func<int> speedMBps, string peerName, Action<string> notify)
    {
        _conn = conn;
        _fileLimitBytes = fileLimitBytes;
        _speedMBps = speedMBps;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create on the UI thread");
        _files = new FileTransfer(fileLimitBytes, peerName, notify,
            (paths, dest) => _ui.Post(_ => Saved?.Invoke(paths, dest), null),
            m =>
            {
                var c = _conn();
                if (c is null || c.IsClosed) return;
                try { c.Send(m); } catch (Exception e) { Log.Error(e); }
            },
            (m, ct) => _conn() is { IsClosed: false } c ? c.SendBulkAsync(m, ct) : throw new IOException("연결이 끊겼어요"),
            speedMBps: speedMBps, isConnected: () => _conn() is { IsClosed: false });
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE: invisible message-only window
        Win32.AddClipboardFormatListener(Handle);
        _ = Task.Run(async () =>
        {
            await foreach (var m in _fileQueue.Reader.ReadAllAsync())
                try { _files.OnMessage(m); } catch (Exception e) { Log.Error(e); }
        });
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == Win32.WM_CLIPBOARDUPDATE)
        {
            try { OnLocalChange(); } catch (Exception e) { Log.Error(e); }
        }
        base.WndProc(ref m);
    }

    void OnLocalChange()
    {
        var data = Retry(Clipboard.GetDataObject);
        if (data is null) return;

        // Files are only announced; they travel when the other PC asks (right-click paste). Offered even while
        // disconnected, so the offer is re-sent on reconnect.
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _files.Offer(files);
            return;
        }
        // Anything else now on the clipboard means the copied files are no longer what is on offer.
        _files.Withdraw();
        var conn = _conn();
        if (conn is null || conn.IsClosed) return;
        if (ReadPng(data) is { } png)
        {
            if (_echo.IsEcho(png)) return;
            Send(conn, new ClipboardImage(png));
            return;
        }
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
        {
            if (_echo.IsEcho(Encoding.UTF8.GetBytes(text))) return;
            Send(conn, new ClipboardText(text));
        }
    }

    static void Send(PeerConnection conn, Message m) =>
        _ = conn.SendBulkAsync(m, CancellationToken.None).AsTask().ContinueWith(t => { if (t.Exception is not null) Log.Error(t.Exception); });

    static byte[]? ReadPng(IDataObject data)
    {
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream ms) return ms.ToArray();
        if (!data.GetDataPresent(DataFormats.Bitmap)) return null;
        using var img = data.GetData(DataFormats.Bitmap) as Image;
        if (img is null) return null;
        using var outMs = new MemoryStream();
        img.Save(outMs, ImageFormat.Png);
        return outMs.ToArray();
    }

    /// <summary>Messages from the peer, on the network thread.</summary>
    public void OnMessage(Message m)
    {
        switch (m)
        {
            case ClipboardText t: _ui.Post(_ => SetText(t.Text), null); break;
            case ClipboardImage i: _ui.Post(_ => SetImage(i.Png), null); break;
            case FileListOffer or FileRequest or FileOfferStart or FileEntry or FileChunk or FileOfferEnd or FileCancel: _fileQueue.Writer.TryWrite(m); break;
            case PeerSettings ps: _files.PeerLimitMB = ps.FileLimitMB; break;
            case PeerSpeed sp: _files.PeerSpeedMBps = sp.MBps; break;
            case Hello h: PeerBuild?.Invoke(h.Build); break;
        }
    }

    public Core.Files.TransferProgress? Progress => _files.Progress;
    public int? PeerLimitMB => _files.PeerLimitMB;
    public int? PeerSpeedMBps => _files.PeerSpeedMBps;
    public void OnDisconnected() => _files.OnDisconnected();
    public Core.Protocol.FileListOffer? Pending => _files.Pending;
    public RequestResult Request(string folder, Guid offerId) => _files.Request(folder, offerId);
    public bool CancelAll() => _files.CancelAll();

    /// <summary>Tells the peer our copy limit and speed limit; call on connect and whenever it changes.</summary>
    public void SendSettings()
    {
        var conn = _conn();
        if (conn is null || conn.IsClosed) return;
        try
        {
            conn.Send(new Hello(BuildInfo.Build));
            conn.Send(new PeerSettings((int)(_fileLimitBytes() >> 20)));
            conn.Send(new PeerSpeed(_speedMBps()));
        }
        catch (Exception e) { Log.Error(e); }
        _files.Reannounce(); // the peer forgot our offer when the link dropped
    }

    void SetText(string text)
    {
        _echo.MarkReceived(Encoding.UTF8.GetBytes(text));
        Retry(() => { Clipboard.SetText(text, TextDataFormat.UnicodeText); return true; });
    }

    void SetImage(byte[] png)
    {
        try
        {
            using var src = new MemoryStream(png);
            using var bmp = new Bitmap(src);
            var obj = new DataObject();
            obj.SetData(DataFormats.Bitmap, true, new Bitmap(bmp));
            obj.SetData("PNG", false, new MemoryStream(png));
            _echo.MarkReceived(png);
            Retry(() => { Clipboard.SetDataObject(obj, true, 5, 100); return true; });
        }
        catch (ArgumentException e) { Log.Error(e); } // not a valid image
    }

    /// <summary>Another app may hold the clipboard open for a moment.</summary>
    static T? Retry<T>(Func<T> f)
    {
        for (int i = 0; ; i++)
        {
            try { return f(); }
            catch (ExternalException) when (i < 4) { Thread.Sleep(100); }
            catch (ExternalException e) { Log.Error(e); return default; }
        }
    }

    public void Dispose()
    {
        _fileQueue.Writer.TryComplete();
        Win32.RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}
