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

    public TrayContext(AppConfig cfg)
    {
        _cfg = cfg;
        _icon.Icon = _disconnected;
        _icon.Text = "Monitor Bridge";
        _icon.ContextMenuStrip = new ContextMenuStrip();
        var limit = new ToolStripMenuItem { Enabled = false };
        _icon.ContextMenuStrip.Items.Add(limit);
        _icon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        // Read on every open: the limit changes when settings are saved.
        _icon.ContextMenuStrip.Opening += (_, _) => limit.Text = _clipboard?.PeerLimitMB is { } peer
            ? $"파일 복사 한도: 이 PC {_cfg.FileSizeLimitMB:N0}MB · 상대 PC {peer:N0}MB (최대 {SettingsForm.MaxFileLimitMB:N0}MB)"
            : $"파일 복사 한도: {_cfg.FileSizeLimitMB:N0}MB (최대 {SettingsForm.MaxFileLimitMB:N0}MB)";
        _icon.ContextMenuStrip.Items.Add("설정", null, (_, _) => ShowSettings());
        _icon.ContextMenuStrip.Items.Add("종료", null, (_, _) => Exit());
        _icon.DoubleClick += (_, _) => ShowSettings();
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
            _clipboard = new ClipboardSync(() => (_host?.Connection ?? _receiver?.Connection), () => (long)_cfg.FileSizeLimitMB * 1024 * 1024, _cfg.Role == Role.Host ? "송출컴" : "게임컴", Notify);
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

    void Notify(string text) => OnUi(() => _icon.ShowBalloonTip(5000, "Monitor Bridge", text, ToolTipIcon.Info));

    void OnUi(Action a) => _ui.Post(_ => a(), null);

    void ShowSettings()
    {
        if (_settings is not null) { _settings.Activate(); return; }
        var layout = _host?.Layout ?? (_cfg.Role == Role.Host || _receiver is null
            ? new Core.Layout.VirtualLayout(MonitorEnumerator.GetMonitors(), _cfg.Placements)
            : null);
        _settings = new SettingsForm(_cfg, layout, MonitorEnumerator.PcId, _host?.Connection is not null);
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
