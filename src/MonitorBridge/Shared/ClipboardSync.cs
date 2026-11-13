using System.Collections.Specialized;
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

    /// <param name="peerName">"게임컴" or "송출컴": the other PC, as people call it in notices.</param>
    public ClipboardSync(Func<PeerConnection?> conn, Func<long> fileLimitBytes, string peerName, Action<string> notify)
    {
        _conn = conn;
        _fileLimitBytes = fileLimitBytes;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create on the UI thread");
        _files = new FileTransfer(fileLimitBytes, peerName, notify, paths => _ui.Post(_ => SetFiles(paths), null));
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
        var conn = _conn();
        if (conn is null || conn.IsClosed) return;
        var data = Retry(Clipboard.GetDataObject);
        if (data is null) return;

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (_echo.IsEcho(Key(files))) return;
            _files.Send(conn, files);
            return;
        }
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

    static byte[] Key(IEnumerable<string> paths) => Encoding.UTF8.GetBytes(string.Join("\n", paths.Order(StringComparer.OrdinalIgnoreCase)));

    /// <summary>Messages from the peer, on the network thread.</summary>
    public void OnMessage(Message m)
    {
        switch (m)
        {
            case ClipboardText t: _ui.Post(_ => SetText(t.Text), null); break;
            case ClipboardImage i: _ui.Post(_ => SetImage(i.Png), null); break;
            case FileOfferStart or FileEntry or FileChunk or FileOfferEnd or FileCancel: _fileQueue.Writer.TryWrite(m); break;
            case PeerSettings ps: _files.PeerLimitMB = ps.FileLimitMB; break;
        }
    }

    public Core.Files.TransferProgress? Progress => _files.Progress;
    public int? PeerLimitMB => _files.PeerLimitMB;
    public void OnDisconnected() => _files.OnDisconnected();

    /// <summary>Tells the peer our copy limit; call on connect and whenever it changes.</summary>
    public void SendSettings()
    {
        var conn = _conn();
        if (conn is null || conn.IsClosed) return;
        try { conn.Send(new PeerSettings((int)(_fileLimitBytes() >> 20))); }
        catch (Exception e) { Log.Error(e); }
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

    void SetFiles(IReadOnlyList<string> paths)
    {
        _echo.MarkReceived(Key(paths));
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        Retry(() => { Clipboard.SetFileDropList(list); return true; });
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
