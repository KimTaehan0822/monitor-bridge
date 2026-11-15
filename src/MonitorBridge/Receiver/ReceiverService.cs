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
    readonly AuthLimiter _limiter = new(5, TimeSpan.FromMinutes(3)); // enhanced security mode only
    volatile bool _modeWarned; // the mode mismatch notice is shown once until a connection succeeds
    Host.LowLevelHooks? _localMouse;

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
        // Cursor stays visible until a game PC connects, so a local mouse works during setup.
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _listener = new TcpListener(IPAddress.Any, _cfg.Port);
        _listener.Start();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _localMouse = new Host.LowLevelHooks(OnLocalInput);
        _localMouse.Install();
    }

    /// <summary>
    /// This PC's own mouse moved (the hook skips input we inject for the game PC): show the cursor again,
    /// so the stream PC stays usable even when the link misbehaves. Watch only, never swallow.
    /// </summary>
    static bool OnLocalInput(Host.HookEvent e)
    {
        // Restore reloads the cursor scheme, too slow for a hook callback: do it off the hook thread.
        if (e is not Host.HookKey && CursorHider.IsHidden) ThreadPool.QueueUserWorkItem(_ => CursorHider.Restore());
        return false;
    }

    void HideCursor()
    {
        if (_cfg.HideReceiverCursor) CursorHider.Hide();
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
        var ip = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
        bool enhanced = _cfg.EnhancedSecurity;
        if (enhanced && _limiter.IsBlocked(ip))
        {
            // Hang up before the password check, so repeated tries cost this PC nothing.
            Log.Info($"blocked {ip}: too many wrong passwords");
            tcp.Dispose();
            return;
        }
        try
        {
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(TimeSpan.FromSeconds(10));
            conn = new PeerConnection(await SecureChannel.AcceptAsync(tcp.GetStream(), _cfg.Password, hs.Token, enhanced));
        }
        catch (SecurityModeMismatchException e)
        {
            // The password was right: not a failed try.
            Log.Info($"rejected {ip}: {e.Message}");
            if (!_modeWarned) _notify(e.PeerEnhanced
                ? "게임컴에서 향상된 보안 모드를 켰어요. 이 PC 설정에서도 켜 주세요."
                : "게임컴에서도 향상된 보안 모드를 켜 주세요. 두 PC 모두 켜야 연결돼요.");
            _modeWarned = true;
            tcp.Dispose();
            return;
        }
        catch (AuthenticationException e)
        {
            Log.Info($"rejected {ip}: {e.Message}");
            // A dropped handshake (port scanner, network blip) is not a password problem; only a real mismatch is reported.
            if (e.InnerException is null)
            {
                if (!enhanced) _notify("다른 비밀번호로 연결하려는 시도가 있었어요. 게임컴 설정을 확인해 주세요.");
                else if (_limiter.Fail(ip))
                {
                    Log.Info($"blocking {ip} for 3 minutes");
                    _notify($"{ip}에서 비밀번호가 5번 틀려서 3분 동안 연결을 막았어요. 3분 뒤에 다시 받아요. 게임컴이라면 비밀번호를 확인해 주세요.");
                }
            }
            tcp.Dispose();
            return;
        }
        catch (Exception e) { Log.Error(e); tcp.Dispose(); return; }
        if (enhanced) _limiter.Succeed(ip);
        _modeWarned = false;

        // A new game PC connection replaces the old one.
        _connCts?.Cancel();
        var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connCts = mine;
        conn.Received += OnReceived;
        _conn = conn;
        HideCursor(); // the game PC holds the cursor until it sends CursorEnter
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
                CursorHider.Restore();
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
            case CursorLeave: HideCursor(); break;
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
        _localMouse?.Dispose();
        _injector.ReleaseAll();
        CursorHider.Restore();
    }
}
