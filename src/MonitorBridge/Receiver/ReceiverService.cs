using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Win32;
using MonitorBridge.Core.Config;
using MonitorBridge.Core.Net;
using MonitorBridge.Core.Protocol;
using Message = MonitorBridge.Core.Protocol.Message;
using MonitorBridge.Shared;

namespace MonitorBridge.Receiver;

/// <summary>Stream PC side: waits for the game PC and replays its cursor and input.</summary>
internal sealed class ReceiverService : IDisposable
{
    readonly AppConfig _cfg;
    readonly Action<string> _notify;
    readonly CancellationTokenSource _cts = new();
    readonly InputInjector _injector = new();
    TcpListener? _listener;
    volatile PeerConnection? _conn;
    CancellationTokenSource? _connCts;

    public event Action<bool>? ConnectedChanged;
    public event Action<Message>? MessageReceived;
    public PeerConnection? Connection => _conn;

    public ReceiverService(AppConfig cfg, Action<string> notify)
    {
        _cfg = cfg;
        _notify = notify;
    }

    public void Start()
    {
        // No cursor on this PC until the game PC hands it over (spec 6.3).
        CursorHider.Hide();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _listener = new TcpListener(IPAddress.Any, _cfg.Port);
        _listener.Start();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Log.Error(e); continue; }
            _ = Task.Run(() => Serve(tcp, ct));
        }
    }

    async Task Serve(TcpClient tcp, CancellationToken ct)
    {
        tcp.NoDelay = true;
        tcp.SendBufferSize = 64 * 1024;
        PeerConnection conn;
        try
        {
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(TimeSpan.FromSeconds(10));
            conn = new PeerConnection(await SecureChannel.AcceptAsync(tcp.GetStream(), _cfg.Password, hs.Token));
        }
        catch (AuthenticationException e)
        {
            Log.Info($"rejected {tcp.Client.RemoteEndPoint}: {e.Message}");
            // A dropped handshake (port scanner, network blip) is not a password problem; only a real mismatch is reported.
            if (e.InnerException is null) _notify("다른 비밀번호로 연결하려는 시도가 있었어요. 게임컴 설정을 확인해 주세요.");
            tcp.Dispose();
            return;
        }
        catch (Exception e) { Log.Error(e); tcp.Dispose(); return; }

        // A new game PC connection replaces the old one.
        _connCts?.Cancel();
        var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connCts = mine;
        conn.Received += OnReceived;
        _conn = conn;
        Log.Info($"host connected from {tcp.Client.RemoteEndPoint}");
        ConnectedChanged?.Invoke(true);
        conn.Send(new MonitorList(MonitorEnumerator.GetMonitors().ToArray()));
        try { await conn.RunAsync(mine.Token); }
        catch (Exception e) { Log.Error(e); }
        finally
        {
            tcp.Dispose();
            if (ReferenceEquals(_conn, conn))
            {
                _conn = null;
                _injector.ReleaseAll();
                CursorHider.Hide();
                Log.Info("host disconnected");
                ConnectedChanged?.Invoke(false);
            }
        }
    }

    void OnReceived(Message m)
    {
        switch (m)
        {
            case CursorEnter e: CursorHider.Restore(); _injector.MoveTo(e.OsX, e.OsY); break;
            case CursorMove mv: _injector.MoveTo(mv.OsX, mv.OsY); break;
            case CursorLeave: CursorHider.Hide(); break;
            case MouseButton b: _injector.Button(b.Button, b.Down); break;
            case MouseWheel w: _injector.Wheel(w.Horizontal, w.Delta); break;
            case Key k: _injector.Key(k.ScanCode, k.Extended, k.Down, k.Vk); break;
            case ReleaseAll: _injector.ReleaseAll(); break;
            default: MessageReceived?.Invoke(m); break;
        }
    }

    void OnDisplayChanged(object? sender, EventArgs e) =>
        _conn?.Send(new MonitorList(MonitorEnumerator.GetMonitors().ToArray()));

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _cts.Cancel();
        _listener?.Stop();
        _injector.ReleaseAll();
        CursorHider.Restore();
    }
}
