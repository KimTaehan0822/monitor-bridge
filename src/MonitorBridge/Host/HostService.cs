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
    readonly System.Threading.Timer _watchdog;
    InputRouter _router;
    IReadOnlyList<MonitorInfo> _remoteMonitors = Array.Empty<MonitorInfo>();
    HashSet<string> _missingNotified = new();
    volatile PeerConnection? _conn;
    volatile bool _locked;
    // Hook thread only. Off while disconnected: then every key and move goes straight to other apps.
    bool _routing, _remoteLockShown;
    bool _keysSuspended; // hook thread only: a hotkey box in the settings window is waiting for a key
    bool _relockOnReturn; // hook thread only: left a game-PC lock with quick switch; lock again once back here

    public event Action<HostStatus>? StatusChanged;
    /// <summary>Show a ripple at this host OS point (raised on the hook thread).</summary>
    public event Action<int, int>? Ripple;
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
        _hooks.HotkeyPressed += OnLockedHotkey;
        _watchdog = new System.Threading.Timer(_ => CheckHooks(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _hooks.Install();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _watchdog.Change(2000, 2000);
        _ = Task.Run(() => ConnectLoop(_cts.Token));
        StatusChanged?.Invoke(HostStatus.Disconnected);
    }

    VirtualLayout BuildLayout() => new(MonitorEnumerator.GetMonitors().Concat(_remoteMonitors), _cfg.Placements);

    async Task ConnectLoop(CancellationToken ct)
    {
        bool authWarned = false, modeWarned = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // A small send buffer keeps input frames from queueing behind megabytes of bulk data inside TCP.
                using var tcp = new TcpClient { NoDelay = true, SendBufferSize = 64 * 1024 };
                SecureChannel ch;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await tcp.ConnectAsync(_cfg.ReceiverAddress, _cfg.Port, timeout.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    ch = await SecureChannel.ConnectAsync(tcp.GetStream(), _cfg.Password, timeout.Token, _cfg.EnhancedSecurity);
                }
                var conn = new PeerConnection(ch);
                conn.Received += OnReceived;
                _conn = conn;
                _hooks.Invoke(() =>
                {
                    // Nothing was tracked while disconnected; start from the real cursor and clean hotkey state.
                    _lock?.Reset();
                    _quick?.Reset();
                    Win32.GetCursorPos(out var p);
                    _router.Resync(p.X, p.Y);
                    _routing = true;
                });
                authWarned = modeWarned = false;
                Log.Info($"connected to {_cfg.ReceiverAddress}:{_cfg.Port}");
                StatusChanged?.Invoke(_locked ? HostStatus.Locked : HostStatus.Connected);
                await conn.RunAsync(ct);
            }
            catch (SecurityModeMismatchException e)
            {
                Log.Info(e.Message);
                if (!modeWarned) _notify(e.PeerEnhanced
                    ? "송출컴에서 향상된 보안 모드를 켰어요. 이 PC 설정에서도 켜 주세요."
                    : "송출컴에서도 향상된 보안 모드를 켜 주세요. 두 PC 모두 켜야 연결돼요.");
                modeWarned = true;
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
                _hooks.Invoke(() => { _routing = false; Execute(_router.OnDisconnected()); });
                Log.Info("disconnected");
                StatusChanged?.Invoke(_locked ? HostStatus.Locked : HostStatus.Disconnected);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>On while the settings window waits for a new hotkey, so pressing the current one does not fire it.</summary>
    public void SuspendHotkeys(bool on) => _hooks.Invoke(() =>
    {
        _keysSuspended = on;
        _lock?.Reset(); // key-ups during the capture were not tracked
        _quick?.Reset();
    });

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

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) => _hooks.Invoke(() =>
    {
        // Key-ups typed on the lock screen never reach the hooks; start clean on both PCs.
        _lock?.Reset();
        _quick?.Reset();
        Execute(_router.OnSessionInterrupted());
    });

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
        var missing = _cfg.Placements.Where(p => !known.Contains(p.Key)).ToList();
        // Monitors that sleep or power off disappear for a while: keep their spot so they come back where they were.
        _cfg.Placements = layout.ToPlacements().Concat(missing).ToList();
        var missingKeys = missing.Select(p => p.Key).ToHashSet();
        if (_remoteMonitors.Count > 0 && missing.Count > 0 && !missingKeys.SetEquals(_missingNotified))
            _notify($"모니터 {missing.Count}대가 보이지 않아요. 다시 켜지면 원래 자리로 돌아와요.");
        _missingNotified = missingKeys;
        try { _cfg.Save(AppConfig.DefaultPath); } catch (Exception ex) { Log.Error(ex); }
        Layout = layout;
        _hooks.Invoke(() => Execute(_router.OnLayoutChanged(layout)));
        LayoutChanged?.Invoke(layout);
    }

    // ---- hook thread ----

    bool Handle(HookEvent e)
    {
        if (!_routing) return false;
        switch (e)
        {
            case HookKey k:
                // While a hotkey box is capturing, our own hotkeys must reach it instead of firing.
                bool lockHit = !_keysSuspended && _lock?.OnKey(k.Vk, k.Down) == true;
                bool quickHit = !_keysSuspended && _quick?.OnKey(k.Vk, k.Down) == true;
                // Game PC: unhook entirely (games). Stream PC: keep routing but stop at its monitor edges.
                if (lockHit)
                {
                    if (!_router.IsRemote) Lock();
                    else
                    {
                        // Unlocking on the stream PC ends the whole lock: coming back no longer relocks the game PC.
                        if (!_router.ToggleRemoteLock()) _relockOnReturn = false;
                        Execute(RouterResult.Swallow);
                    }
                    return true;
                }
                if (quickHit)
                {
                    // The lock follows the cursor: leaving a stream-PC lock arrives locked on the game PC.
                    if (_router.RemoteLocked) _relockOnReturn = true;
                    QuickSwitch();
                    return true;
                }
                return Execute(_router.OnKey(k.Scan, k.Extended, k.Down, (ushort)k.Vk));
            case HookMouseMove mv:
                if (!_router.IsRemote) return Execute(_router.OnMouseMove(mv.X, mv.Y));
                // Measure from where the hidden cursor really is: a game may have moved it with SetCursorPos.
                Win32.GetCursorPos(out var cur);
                var park = _router.ParkPoint;
                if (cur.X != park.X || cur.Y != park.Y) Win32.SetCursorPos(park.X, park.Y);
                return Execute(_router.OnMouseMove(park.X + (mv.X - cur.X), park.Y + (mv.Y - cur.Y)));
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
                // Swapping system cursors is slow (it can read cursor files); run it after the hook callback returns.
                case ParkAndHide p: Win32.SetCursorPos(p.OsX, p.OsY); _hooks.Invoke(CursorHider.Hide); break;
                case UnparkAndShow u: Win32.SetCursorPos(u.OsX, u.OsY); _hooks.Invoke(CursorHider.Restore); break;
                case Warp w: Win32.SetCursorPos(w.OsX, w.OsY); break;
            }
        }
        // The stream-PC lock also ends whenever the cursor is sent home (disconnect, lock screen, layout change).
        if (_router.RemoteLocked != _remoteLockShown)
        {
            _remoteLockShown = _router.RemoteLocked;
            StatusChanged?.Invoke(_remoteLockShown ? HostStatus.Locked : _conn is null ? HostStatus.Disconnected : HostStatus.Connected);
        }
        // Back on the game PC by any path (quick switch, pushing an edge, disconnect): lock again for the game.
        if (_relockOnReturn && !_router.IsRemote && !_locked)
        {
            _relockOnReturn = false;
            Lock();
        }
        return r.Block;
    }

    void QuickSwitch()
    {
        var r = _router.QuickSwitch();
        Execute(r);
        foreach (var a in r.Local)
        {
            if (a is ParkAndHide) _conn?.Send(new Highlight()); // after CursorEnter, so the stream PC ripples where the cursor landed
            else if (a is UnparkAndShow u) Ripple?.Invoke(u.OsX, u.OsY);
        }
    }

    void Lock()
    {
        if (_lock is null) return;
        _hooks.Uninstall();
        if (!_hooks.RegisterHotkey(LowLevelHooks.LockHotkeyId, _lock.Hotkey))
        {
            _hooks.Install();
            _notify("가두기 키를 등록하지 못했어요. 다른 프로그램이 같은 키를 쓰고 있을 수 있어요.");
            return;
        }
        // The hooks are off while locked (games), so quick switch needs its own system hotkey too.
        if (_quick is not null && !_hooks.RegisterHotkey(LowLevelHooks.QuickHotkeyId, _quick.Hotkey))
            _notify("가둔 동안 빠른 전환 키를 쓸 수 없어요. 다른 프로그램이 같은 키를 쓰고 있을 수 있어요.");
        _locked = true;
        StatusChanged?.Invoke(HostStatus.Locked);
    }

    /// <summary>Hook thread: a system hotkey fired while the game PC is locked.</summary>
    void OnLockedHotkey(int id)
    {
        if (!_locked) return;
        if (id == LowLevelHooks.QuickHotkeyId)
        {
            if (_conn is null) return; // nowhere to go; stay locked
            Unlock();
            // Ctrl/Alt went down while the hooks were off, so the router never saw them. Tell it, so entering the
            // stream PC releases them here; otherwise the game keeps them pressed.
            foreach (int vk in _quick!.Hotkey.PhysicalKeys())
            {
                if ((Win32.GetAsyncKeyState(vk) & 0x8000) == 0) continue;
                uint sc = Win32.MapVirtualKey((uint)vk, 4); // MAPVK_VK_TO_VSC_EX: 0xE0xx for extended keys
                _router.OnKey((ushort)(sc & 0xFF), (sc & 0xFF00) != 0, true, (ushort)vk);
            }
            QuickSwitch();
            _relockOnReturn = _router.IsRemote;
            // Stay locked on the stream PC too: only the quick-switch key brings the cursor back, not an edge.
            if (_router.IsRemote) { _router.ToggleRemoteLock(); Execute(RouterResult.Swallow); }
            return;
        }
        _relockOnReturn = false; // unlocked on purpose
        Unlock();
    }

    void Unlock()
    {
        _hooks.UnregisterHotkey(LowLevelHooks.LockHotkeyId);
        _hooks.UnregisterHotkey(LowLevelHooks.QuickHotkeyId);
        _lock?.Reset();
        _quick?.Reset();
        // The router saw no moves while locked; start from where the cursor is now.
        Win32.GetCursorPos(out var p);
        _router.Resync(p.X, p.Y);
        _hooks.Install();
        _locked = false;
        StatusChanged?.Invoke(_conn is null ? HostStatus.Disconnected : HostStatus.Connected);
    }

    /// <summary>
    /// Windows silently removes low-level hooks that time out. If input happened that the hooks never saw,
    /// put them back and bring the cursor home.
    /// </summary>
    void CheckHooks()
    {
        if (_locked || !_hooks.Installed) return;
        uint lastInput = Win32.LastInputTick();
        uint lastHook = unchecked((uint)_hooks.LastCallbackTick);
        if (lastInput == 0 || unchecked((int)(lastInput - lastHook)) < 1500) return;
        int unseen = unchecked((int)(lastInput - lastHook));
        _hooks.Invoke(() =>
        {
            Log.Info($"hooks stopped receiving input ({unseen}ms of input unseen); reinstalling: {_hooks.Reinstall()}");
            Execute(_router.OnSessionInterrupted());
        });
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _cts.Cancel();
        _hooks.Invoke(() => Execute(_router.OnDisconnected()));
        _hooks.Dispose();
        CursorHider.Restore();
    }
}
