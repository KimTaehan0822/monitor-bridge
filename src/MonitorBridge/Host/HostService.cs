using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Win32;
using MonitorBridge.Core.Config;
using MonitorBridge.Core.Layout;
using MonitorBridge.Core.Net;
using MonitorBridge.Core.Protocol;
using Message = MonitorBridge.Core.Protocol.Message;
using MonitorBridge.Core.Routing;
using MonitorBridge.Native;
using MonitorBridge.Receiver;
using MonitorBridge.Shared;

namespace MonitorBridge.Host;

internal enum HostStatus { Disconnected, Connected, Locked }

/// <summary>Game PC side: owns the keyboard/mouse, decides where input goes, keeps reconnecting to the receiver.</summary>
internal sealed class HostService : IDisposable
{
    readonly AppConfig _cfg;
    readonly Action<string> _notify;
    readonly CancellationTokenSource _cts = new();
    readonly LowLevelHooks _hooks;
    readonly InputInjector _local = new();
    readonly HotkeyMatcher? _lock, _quick;
    InputRouter _router;
    IReadOnlyList<MonitorInfo> _remoteMonitors = Array.Empty<MonitorInfo>();
    volatile PeerConnection? _conn;
    bool _locked;

    public event Action<HostStatus>? StatusChanged;
    public event Action<Message>? MessageReceived;
    public event Action<VirtualLayout>? LayoutChanged;
    public PeerConnection? Connection => _conn;
    public VirtualLayout Layout { get; private set; }

    public HostService(AppConfig cfg, Action<string> notify)
    {
        _cfg = cfg;
        _notify = notify;
        if (Hotkey.TryParse(cfg.LockHotkey, out var lk)) _lock = new HotkeyMatcher(lk);
        if (Hotkey.TryParse(cfg.QuickSwitchHotkey, out var qk)) _quick = new HotkeyMatcher(qk);
        Layout = BuildLayout();
        Win32.GetCursorPos(out var p);
        _router = new InputRouter(Layout, MonitorEnumerator.PcId, new VPoint(p.X, p.Y));
        _hooks = new LowLevelHooks(Handle);
        _hooks.HotkeyPressed += Unlock;
    }

    public void Start()
    {
        _hooks.Install();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        _ = Task.Run(() => ConnectLoop(_cts.Token));
        StatusChanged?.Invoke(HostStatus.Disconnected);
    }

    VirtualLayout BuildLayout() => new(MonitorEnumerator.GetMonitors().Concat(_remoteMonitors), _cfg.Placements);

    async Task ConnectLoop(CancellationToken ct)
    {
        bool authWarned = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await tcp.ConnectAsync(_cfg.ReceiverAddress, _cfg.Port, timeout.Token);
                }
                var ch = await SecureChannel.ConnectAsync(tcp.GetStream(), _cfg.Password, ct);
                var conn = new PeerConnection(ch);
                conn.Received += OnReceived;
                _conn = conn;
                authWarned = false;
                Log.Info($"connected to {_cfg.ReceiverAddress}:{_cfg.Port}");
                StatusChanged?.Invoke(_locked ? HostStatus.Locked : HostStatus.Connected);
                await conn.RunAsync(ct);
            }
            catch (AuthenticationException e)
            {
                Log.Info(e.Message);
                if (!authWarned) _notify("송출컴과 비밀번호가 달라서 연결하지 못했어요. 설정을 확인해 주세요.");
                authWarned = true;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or IOException) { }
            catch (Exception e) { Log.Error(e); }

            if (_conn is not null)
            {
                _conn = null;
                _remoteMonitors = Array.Empty<MonitorInfo>();
                _hooks.Invoke(() => Execute(_router.OnDisconnected()));
                Log.Info("disconnected");
                StatusChanged?.Invoke(_locked ? HostStatus.Locked : HostStatus.Disconnected);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
        }
    }

    void OnReceived(Message m)
    {
        if (m is MonitorList list)
        {
            _remoteMonitors = list.Monitors;
            ApplyLayout();
        }
        else MessageReceived?.Invoke(m);
    }

    void OnDisplayChanged(object? sender, EventArgs e) => ApplyLayout();

    /// <summary>Rebuilds the layout from current monitors and saved placements; persists placements for new monitors.</summary>
    public void ApplyLayout(IEnumerable<Placement>? placements = null)
    {
        lock (_cfg) ApplyLayoutLocked(placements);
    }

    void ApplyLayoutLocked(IEnumerable<Placement>? placements)
    {
        if (placements is not null) _cfg.Placements = placements.ToList();
        var layout = BuildLayout();
        var known = layout.Monitors.Select(m => m.Info.Key).ToHashSet();
        var lost = _cfg.Placements.Where(p => !known.Contains(p.Key)).ToList();
        if (lost.Count > 0 && _remoteMonitors.Count > 0)
            _notify($"모니터 {lost.Count}대를 찾지 못해서 배치에서 뺐어요.");
        // Keep placements of monitors that are only temporarily absent (e.g. receiver offline).
        _cfg.Placements = layout.ToPlacements().Concat(_remoteMonitors.Count > 0 ? Enumerable.Empty<Placement>() : lost).ToList();
        try { _cfg.Save(AppConfig.DefaultPath); } catch (Exception ex) { Log.Error(ex); }
        Layout = layout;
        _hooks.Invoke(() => Execute(_router.OnLayoutChanged(layout)));
        LayoutChanged?.Invoke(layout);
    }

    // ---- hook thread ----

    bool Handle(HookEvent e)
    {
        switch (e)
        {
            case HookKey k:
                bool lockHit = _lock?.OnKey(k.Vk, k.Down) == true;
                bool quickHit = _quick?.OnKey(k.Vk, k.Down) == true;
                if (lockHit && !_router.IsRemote) { Lock(); return true; }
                if (lockHit) return false; // spec: lock only on the game PC; elsewhere the key is ignored
                if (quickHit) { Execute(_router.QuickSwitch()); return true; }
                return Execute(_router.OnKey(k.Scan, k.Extended, k.Down));
            case HookMouseMove mv: return Execute(_router.OnMouseMove(mv.X, mv.Y));
            case HookMouseButton b: return Execute(_router.OnButton(b.Button, b.Down));
            case HookMouseWheel w: return Execute(_router.OnWheel(w.Horizontal, w.Delta));
            default: return false;
        }
    }

    bool Execute(RouterResult r)
    {
        var conn = _conn;
        foreach (var m in r.ToRemote) conn?.Send(m);
        foreach (var a in r.Local)
        {
            switch (a)
            {
                case ReleaseLocalKeys rel:
                    foreach (var (scan, ext) in rel.Keys) _local.Key(scan, ext, false);
                    foreach (var b in rel.Buttons) _local.Button(b, false);
                    break;
                case ParkAndHide p: Win32.SetCursorPos(p.OsX, p.OsY); CursorHider.Hide(); break;
                case UnparkAndShow u: CursorHider.Restore(); Win32.SetCursorPos(u.OsX, u.OsY); break;
                case Warp w: Win32.SetCursorPos(w.OsX, w.OsY); break;
            }
        }
        return r.Block;
    }

    void Lock()
    {
        if (_lock is null) return;
        _hooks.Uninstall();
        if (!_hooks.RegisterHotkey(_lock.Hotkey))
        {
            _hooks.Install();
            _notify("가두기 키를 등록하지 못했어요. 다른 프로그램이 같은 키를 쓰고 있을 수 있어요.");
            return;
        }
        _locked = true;
        StatusChanged?.Invoke(HostStatus.Locked);
    }

    void Unlock()
    {
        _hooks.UnregisterHotkey();
        _hooks.Install();
        _locked = false;
        StatusChanged?.Invoke(_conn is null ? HostStatus.Disconnected : HostStatus.Connected);
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _cts.Cancel();
        _hooks.Invoke(() => Execute(_router.OnDisconnected()));
        _hooks.Dispose();
        CursorHider.Restore();
    }
}
