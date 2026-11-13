using System.Net;
using System.Net.Sockets;
using MonitorBridge.Core.Config;
using MonitorBridge.Host;
using MonitorBridge.Receiver;
using MonitorBridge.Shared;

namespace MonitorBridge.UI;

/// <summary>Tray icon, menu and the lifetime of the running service.</summary>
internal sealed class TrayContext : ApplicationContext
{
    readonly SynchronizationContext _ui = new WindowsFormsSynchronizationContext();
    readonly NotifyIcon _icon = new();
    readonly Icon _connected = Icons.Make(ColorTranslator.FromHtml("#1F8A84"));
    readonly Icon _disconnected = Icons.Make(Color.Gray);
    readonly Icon _locked = Icons.Make(ColorTranslator.FromHtml("#C98A00"), locked: true);
    AppConfig _cfg;
    HostService? _host;
    ReceiverService? _receiver;
    SettingsForm? _settings;
    ClipboardSync? _clipboard;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    string _statusText = "Monitor Bridge";
    Core.Net.PeerConnection? _lastConn;
    long _finishedAt;
    string? _clickPath; // what clicking the current balloon opens
    IDisposable? _pasteChannel;
    bool _asking; // one paste question at a time (several selected folders launch several requests)
    string? _peerBuild;     // what the other PC runs, once it told us on this connection
    bool _buildWarned;      // the mismatch notice is shown once per connection
    string PeerName => _cfg.Role == Role.Host ? "송출컴" : "게임컴";

    public TrayContext(AppConfig cfg)
    {
        _cfg = cfg;
        _icon.Icon = _disconnected;
        _icon.Text = "Monitor Bridge";
        _icon.ContextMenuStrip = new ContextMenuStrip();
        var limit = new ToolStripMenuItem { Enabled = false };
        var version = new ToolStripMenuItem { Enabled = false };
        _icon.ContextMenuStrip.Opening += (_, _) => version.Text = _peerBuild is null || _peerBuild == BuildInfo.Build
            ? $"버전: {BuildInfo.Build}"
            : $"버전: 이 PC {BuildInfo.Build} · 상대 PC {_peerBuild} (서로 달라요)";
        _icon.ContextMenuStrip.Items.Add(version);
        _icon.ContextMenuStrip.Items.Add(limit);
        _icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        // Read on every open: the limit changes when settings are saved.
        _icon.ContextMenuStrip.Opening += (_, _) => limit.Text = _clipboard?.PeerLimitMB is { } peer
            ? $"파일 복사 한도: 이 PC {_cfg.FileSizeLimitMB:N0}MB · 상대 PC {peer:N0}MB (최대 {SettingsForm.MaxFileLimitMB:N0}MB)"
            : $"파일 복사 한도: {_cfg.FileSizeLimitMB:N0}MB (최대 {SettingsForm.MaxFileLimitMB:N0}MB)";
        var cancelItem = new ToolStripMenuItem("파일 전송 취소", null, (_, _) => CancelTransfer());
        _icon.ContextMenuStrip.Opening += (_, _) => cancelItem.Enabled = _clipboard?.Progress?.State == Core.Files.TransferState.Running;
        _icon.ContextMenuStrip.Items.Add("파일 전송 보기", null, (_, _) => ShowTransfer());
        _icon.ContextMenuStrip.Items.Add(cancelItem);
        _icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add("설정", null, (_, _) => ShowSettings());
        _icon.ContextMenuStrip.Items.Add("종료", null, (_, _) => Exit());
        _icon.DoubleClick += (_, _) => ShowSettings();
        // One click shows the transfer window, but only while there is a transfer to look at.
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && VisibleProgress() is not null) ShowTransfer(); };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_clickPath is null) { if (VisibleProgress() is not null) ShowTransfer(); return; }
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_clickPath}\""); }
            catch (Exception e) { Log.Error(e); }
        };
        try { ShellMenu.Register(Environment.ProcessPath!); }
        catch (Exception e) { Log.Error(e); } // the right-click item is a convenience; never block startup
        _pasteChannel = PasteChannel.Start(PasteChannel.DefaultDir, folder => OnUi(() => OnPaste(folder)));
        _icon.Visible = true;
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        if (string.IsNullOrEmpty(cfg.Password)) _ui.Post(_ => ShowSettings(), null);
        else StartServices();
    }

    void StartServices()
    {
        try
        {
            _clipboard = new ClipboardSync(() => (_host?.Connection ?? _receiver?.Connection), () => (long)_cfg.FileSizeLimitMB * 1024 * 1024,
                PeerName, Notify);
            _clipboard.PeerBuild += b => OnUi(() => OnPeerBuild(b));
            _clipboard.Saved += (paths, dest) =>
            {
                if (paths.Count == 0) return;
                var name = Path.GetFileName(paths[0]) + (paths.Count > 1 ? $" 외 {paths.Count - 1}개" : "");
                _clickPath = paths[0];
                _icon.ShowBalloonTip(5000, "Monitor Bridge", $"다 받았어요: {name} → {dest}. 누르면 폴더를 열어요.", ToolTipIcon.Info);
            };
            if (_cfg.Role == Role.Host)
            {
                _host = new HostService(_cfg, Notify);
                _host.StatusChanged += s => OnUi(() => SetStatus(s));
                _host.MessageReceived += _clipboard.OnMessage;
                _host.Ripple += (x, y) => OnUi(() => RippleWindow.Play(x, y));
                _host.Start();
            }
            else
            {
                _receiver = new ReceiverService(_cfg, Notify);
                _receiver.ConnectedChanged += c => OnUi(() => SetStatus(c ? HostStatus.Connected : HostStatus.Disconnected));
                _receiver.MessageReceived += _clipboard.OnMessage;
                // The game PC already moved the cursor here with CursorEnter.
                _receiver.MessageReceived += m => { if (m is Core.Protocol.Highlight) OnUi(() => { Native.Win32.GetCursorPos(out var p); RippleWindow.Play(p.X, p.Y); }); };
                _receiver.Start();
                SetStatus(HostStatus.Disconnected);
            }
        }
        catch (SocketException e)
        {
            Log.Error(e);
            Notify($"포트 {_cfg.Port}을(를) 열지 못했어요. 다른 프로그램이 쓰고 있는지 확인하거나 설정에서 포트를 바꿔 주세요.");
        }
    }

    void StopServices()
    {
        _host?.Dispose();
        _receiver?.Dispose();
        _clipboard?.Dispose();
        _clipboard = null;
        _host = null;
        _receiver = null;
    }

    void SetStatus(HostStatus s)
    {
        var role = _cfg.Role == Role.Host ? "게임컴" : "송출컴";
        (_icon.Icon, _statusText) = s switch
        {
            HostStatus.Connected => (_connected, $"Monitor Bridge ({role}) - 연결됨"),
            HostStatus.Locked => (_locked, $"Monitor Bridge ({role}) - 커서 가둠"),
            _ => (_disconnected, $"Monitor Bridge ({role}) - 연결 기다리는 중"),
        };
        _icon.Text = Core.Files.TransferProgress.Tooltip(_statusText, VisibleProgress()?.Describe());
    }

    /// <summary>Every second: notice connection changes and refresh the transfer line in the tray tooltip.</summary>
    void OnTick()
    {
        var conn = _host?.Connection ?? _receiver?.Connection;
        if (conn is { IsClosed: true }) conn = null;
        // Compare the connection object, not a flag: a drop and reconnect inside one second is still a new link.
        if (!ReferenceEquals(conn, _lastConn))
        {
            if (_lastConn is not null) _clipboard?.OnDisconnected();
            _peerBuild = null;
            _buildWarned = false;
            _lastConn = conn;
            if (conn is not null) _clipboard?.SendSettings();
        }
        _icon.Text = Core.Files.TransferProgress.Tooltip(_statusText, VisibleProgress()?.Describe());
    }

    /// <summary>The running transfer, or a finished one for a minute so people can see how it ended.</summary>
    Core.Files.TransferProgress? VisibleProgress()
    {
        var p = _clipboard?.Progress;
        if (p is null || p.State == Core.Files.TransferState.Running) { _finishedAt = 0; return p; }
        long now = Environment.TickCount64;
        if (_finishedAt == 0) _finishedAt = now;
        return now - _finishedAt < 60_000 ? p : null;
    }

    /// <summary>Different builds keep working for mouse and keyboard (unknown messages are skipped); say so once.</summary>
    void OnPeerBuild(string build)
    {
        _peerBuild = build;
        if (build == BuildInfo.Build || _buildWarned) return;
        _buildWarned = true;
        Notify($"두 PC의 Monitor Bridge 버전이 달라요. 이 PC: {BuildInfo.Build}, 상대 PC: {build}. 키보드·마우스는 그대로 쓸 수 있지만 일부 기능이 동작하지 않을 수 있어요. 두 PC를 같은 버전으로 맞춰 주세요.");
    }

    void ShowTransfer() =>
        TransferWindow.ShowOrFocus(() => _clipboard?.Progress, () => _clipboard?.CancelAll() ?? false);

    void CancelTransfer() => _clipboard?.CancelAll();

    /// <summary>Right-click "붙여넣기" in Explorer landed here with the folder it was used on.</summary>
    void OnPaste(string folder)
    {
        if (_asking) return;
        if (!Directory.Exists(folder))
        {
            // Quick access, libraries and similar views pass a virtual location, not a folder.
            Notify("이 위치에는 붙여넣을 수 없어요. 일반 폴더나 바탕화면에서 우클릭해 주세요.");
            return;
        }
        if (_clipboard?.Pending is not { } offer)
        {
            Notify("상대 PC에서 복사한 파일이 없어요. 상대 PC에서 파일을 먼저 복사(Ctrl+C)해 주세요.");
            return;
        }
        bool yes;
        _asking = true;
        try { yes = PasteConfirm.Ask(PeerName, offer, folder); }
        finally { _asking = false; }
        if (!yes || _clipboard is null) return;
        switch (_clipboard.Request(folder, offer.Id))
        {
            case RequestResult.Changed: Notify("그 사이에 상대 PC에서 다른 파일을 복사했어요. 다시 우클릭해서 가져와 주세요."); break;
            case RequestResult.Busy: Notify("이미 파일을 주고받는 중이에요. 끝나거나 취소한 뒤에 다시 가져와 주세요."); break;
            case RequestResult.TooBig: Notify("파일이 복사 한도보다 커서 가져올 수 없어요. 두 PC 설정의 파일 복사 한도를 확인해 주세요."); break;
            case RequestResult.NothingOffered: Notify("상대 PC에서 복사한 파일이 없어요. 상대 PC에서 파일을 먼저 복사(Ctrl+C)해 주세요."); break;
        }
    }

    void Notify(string text) => OnUi(() => { _clickPath = null; _icon.ShowBalloonTip(5000, "Monitor Bridge", text, ToolTipIcon.Info); });

    void OnUi(Action a) => _ui.Post(_ => a(), null);

    void ShowSettings()
    {
        if (_settings is not null) { _settings.Activate(); return; }
        var layout = _host?.Layout ?? (_cfg.Role == Role.Host || _receiver is null
            ? new Core.Layout.VirtualLayout(MonitorEnumerator.GetMonitors(), _cfg.Placements)
            : null);
        _settings = new SettingsForm(_cfg, layout, MonitorEnumerator.PcId, _host?.Connection is not null,
            () => _clipboard?.PeerLimitMB, VisibleProgress);
        if (_cfg.Role == Role.Receiver || string.IsNullOrEmpty(_cfg.Password)) _settings.ShowLocalAddresses($"이 PC의 IP: {string.Join(", ", LocalAddresses())}  (게임컴 설정의 '송출컴 IP'에 넣으세요)");
        try
        {
            if (_settings.ShowDialog() != DialogResult.OK || _settings.Result is null) return;
            var next = _settings.Result;
            if (next.AutoStart != AutoStart.IsEnabled() && !AutoStart.Set(next.AutoStart))
            {
                next.AutoStart = AutoStart.IsEnabled();
                Notify("자동 실행 설정을 바꾸지 못했어요. 관리자 권한 확인 창에서 '예'를 눌러야 해요.");
            }
            if (_clipboard is not null && SameConnection(_cfg, next))
            {
                // Nothing the link depends on changed: apply in place and keep the connection.
                // The services hold this same _cfg object, so update it rather than replacing it.
                _cfg.FileSizeLimitMB = next.FileSizeLimitMB;
                _cfg.AutoStart = next.AutoStart;
                if (_host is not null) _host.ApplyLayout(next.Placements);
                else _cfg.Placements = next.Placements;
                _cfg.Save(AppConfig.DefaultPath);
                _clipboard.SendSettings();
                return;
            }
            next.Save(AppConfig.DefaultPath);
            StopServices();
            _cfg = next;
            StartServices();
        }
        catch (Exception e)
        {
            Log.Error(e);
            Notify("설정을 저장하지 못했어요: " + e.Message);
        }
        finally
        {
            _settings.Dispose();
            _settings = null;
        }
    }

    static bool SameConnection(AppConfig a, AppConfig b) =>
        a.Role == b.Role && a.ReceiverAddress == b.ReceiverAddress && a.Port == b.Port && a.Password == b.Password
        && a.LockHotkey == b.LockHotkey && a.QuickSwitchHotkey == b.QuickSwitchHotkey;

    static IEnumerable<string> LocalAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString());
        }
        catch (SocketException) { return new[] { "알 수 없음" }; }
    }

    void Exit()
    {
        _tick.Stop();
        _pasteChannel?.Dispose();
        StopServices();
        CursorHider.Restore();
        _icon.Visible = false;
        ExitThread();
    }
}

internal static class Icons
{
    /// <summary>Two monitors side by side; a dark bar underneath when the cursor is locked to the game PC.</summary>
    public static Icon Make(Color c, bool locked = false)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(c);
            g.FillRectangle(b, 1, 6, 14, 12);
            g.FillRectangle(b, 17, 6, 14, 12);
            g.FillRectangle(b, 6, 19, 4, 3);
            g.FillRectangle(b, 22, 19, 4, 3);
            if (locked) g.FillRectangle(Brushes.Black, 2, 25, 28, 5);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
