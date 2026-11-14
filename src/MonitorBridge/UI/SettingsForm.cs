using MonitorBridge.Core.Config;
using MonitorBridge.Core.Files;
using MonitorBridge.Core.Layout;

namespace MonitorBridge.UI;

/// <summary>Edits a copy of the config. DialogResult.OK means <see cref="Result"/> holds the new settings.</summary>
internal sealed class SettingsForm : Form
{
    readonly RadioButton _host = new() { Text = "게임컴 (키보드·마우스가 연결된 PC)", AutoSize = true };
    readonly RadioButton _receiver = new() { Text = "송출컴 (게임컴이 조작하는 PC)", AutoSize = true };
    readonly TextBox _address = new() { Width = 160, PlaceholderText = "예: 192.168.0.10" };
    readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    readonly TextBox _password = new() { Width = 160, UseSystemPasswordChar = true };
    readonly HotkeyBox _lockKey = new();
    readonly HotkeyBox _quickKey = new();
    public const int MaxFileLimitMB = 100_000;
    readonly NumericUpDown _fileLimit = new() { Minimum = 1, Maximum = MaxFileLimitMB, Width = 80 };
    public const int MaxSpeedMBps = 1_000;
    readonly NumericUpDown _speedLimit = new() { Minimum = 0, Maximum = MaxSpeedMBps, Width = 80 };
    readonly LinkLabel _guideLink = new() { Text = GuideClosed, AutoSize = true };
    bool _guideOpen;
    const string GuideClosed ="속도 기준 보기 ▸", GuideOpen = "속도 기준 접기 ▾";
    readonly Label _speedGuide = new()
    {
        AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Visible = false,
        Text = "· 1MB/s는 인터넷 속도로 8Mbps예요.\n"
            + "· 1기가(1000Mbps) 환경: 최대 약 110MB/s.\n"
            + "   공유기, 랜선, 두 PC의 랜카드가 모두 1기가를 지원해야 해요.\n"
            + "· 100메가(100Mbps) 환경: 최대 약 11MB/s.\n"
            + "   셋 중 하나라도 100메가면 전체가 100메가로 동작해요.\n"
            + "· 와이파이는 속도가 들쭉날쭉해요.\n"
            + "· 방송 업로드: FHD 8000kbps 기준 1시간에 약 4GB(여유 있게 잡은 값), 약 1.1MB/s예요.\n"
            + "   랜선에 자리가 남아 있어야 방송이 끊기지 않아요.\n\n"
            + "추천 값\n"
            + "· 1기가 환경에서 방송 중: 30MB/s (기본값)\n"
            + "· 100메가 환경, 와이파이, 잘 모를 때: 5~10MB/s\n"
            + "· 방송하지 않을 때: 0 (제한 없음)",
    };
    readonly CheckBox _autoStart = new() { Text = "윈도우 로그인 시 관리자 권한으로 자동 실행", AutoSize = true };
    static readonly Color Warn = ColorTranslator.FromHtml("#C0392B");
    readonly Label _error = new() { AutoSize = true, ForeColor = Warn };
    readonly Label _limitWarn = new() { AutoSize = true, ForeColor = Warn, Visible = false };
    readonly Label _speedWarn = new() { AutoSize = true, ForeColor = Warn, Visible = false };
    readonly Label _adminWarn = new()
    {
        AutoSize = true, ForeColor = Warn, Visible = false,
        Text = "지금 관리자 권한 없이 실행 중이라 설치·제거 창, 관리자로 켠 OBS 같은 프로그램은 조작할 수 없어요.\n"
            + "위 자동 실행을 켜고 PC를 다시 시작하거나, exe를 우클릭 → '관리자 권한으로 실행'으로 켜 주세요.",
    };
    readonly LinkLabel _update = new() { AutoSize = true, Visible = false, Margin = new Padding(0, 10, 0, 0) };
    readonly CancellationTokenSource _closing = new();
    readonly ProgressBar _bar = new() { Width = 300, Height = 8, Style = ProgressBarStyle.Continuous, Maximum = 100 };
    readonly Label _transfer = new() { AutoSize = true };
    readonly System.Windows.Forms.Timer _live = new() { Interval = 1000 };
    readonly Func<int?>? _peerLimitMB;
    readonly Func<int?>? _peerSpeedMBps;
    readonly Func<TransferProgress?>? _progress;
    readonly Button _save = new() { Text = "저장", AutoSize = true };
    readonly LayoutCanvas? _canvas;
    readonly Control[] _hostOnly;
    readonly AppConfig _source;

    public AppConfig? Result { get; private set; }
    /// <summary>True while a hotkey box is waiting for a key: the game PC must not act on its own hotkeys then.</summary>
    public event Action<bool>? CapturingKeys;

    public SettingsForm(AppConfig cfg, VirtualLayout? layout, string hostPcId, bool remoteConnected,
        Func<int?>? peerLimitMB = null, Func<TransferProgress?>? progress = null, Func<int?>? peerSpeedMBps = null)
    {
        _source = cfg;
        _peerLimitMB = peerLimitMB;
        _peerSpeedMBps = peerSpeedMBps;
        _progress = progress;
        Text = "Monitor Bridge 설정";
        Font = new Font("맑은 고딕", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        _host.Checked = cfg.Role == Role.Host;
        _receiver.Checked = cfg.Role == Role.Receiver;
        _address.Text = cfg.ReceiverAddress;
        _port.Value = cfg.Port;
        _password.Text = cfg.Password;
        _lockKey.Value = cfg.LockHotkey;
        _quickKey.Value = cfg.QuickSwitchHotkey;
        _fileLimit.Value = Math.Clamp(cfg.FileSizeLimitMB, 1, MaxFileLimitMB);
        _speedLimit.Value = Math.Clamp(cfg.FileSpeedLimitMBps, 0, MaxSpeedMBps);
        _autoStart.Checked = cfg.AutoStart;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // Every row gets its own fixed cells. Adding in flow order would let a hidden control's neighbours slide
        // into its cell, so labels and inputs drifted apart whenever a row was hidden (stream PC, limit warning).
        int row = 0;
        Label? Row(string label, Control c)
        {
            Label? l = null;
            if (label.Length > 0)
            {
                l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };
                grid.Controls.Add(l, 0, row);
            }
            c.Margin = new Padding(0, 3, 0, 3);
            grid.Controls.Add(c, 1, row);
            row++;
            return l;
        }
        Control Inline(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            foreach (var c in cs) { c.Margin = new Padding(0, 0, 8, 0); f.Controls.Add(c); }
            return f;
        }
        Label Unit(string s) => new() { Text = s, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };

        var roleBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        roleBox.Controls.Add(_host);
        roleBox.Controls.Add(_receiver);
        Row("이 PC는", roleBox);
        var addressRow = Inline(_address, Unit("포트"), _port);
        var addressLabel = Row("송출컴 IP", addressRow)!;
        Row("비밀번호", Inline(_password, Unit("두 PC에 같은 비밀번호를 넣으세요")));
        Button Clear(HotkeyBox box) { var b = new Button { Text = "지우기", AutoSize = true }; b.Click += (_, _) => box.Clear(); return b; }
        var lockRow = Inline(_lockKey, Clear(_lockKey), Unit("커서를 지금 있는 PC에 가둡니다"));
        var lockLabel = Row("가두기 키", lockRow)!;
        var quickRow = Inline(_quickKey, Clear(_quickKey), Unit("다른 PC로 바로 넘어갑니다"));
        var quickLabel = Row("빠른 전환 키", quickRow)!;
        var keyHint = new Label { Text = "키를 비워 두면 그 기능은 쓰지 않아요.", AutoSize = true, ForeColor = SystemColors.GrayText };
        Row("", keyHint);
        Row("파일 복사 한도", Inline(_fileLimit, Unit($"MB까지 상대 PC로 보냅니다 (최대 {MaxFileLimitMB:N0}MB)")));
        Row("", _limitWarn);
        Row("파일 전송 속도", Inline(_speedLimit, Unit("MB/s까지 보냅니다 (0 = 제한 없음)")));
        Row("", _speedWarn);
        Row("", _guideLink);
        Row("", _speedGuide);
        _guideLink.LinkClicked += (_, _) => ToggleSpeedGuide();
        Row("", _autoStart);
        Row("", _adminWarn);
        var transferBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        _bar.Margin = new Padding(0, 4, 0, 4);
        _transfer.Margin = Padding.Empty;
        transferBox.Controls.Add(_bar);
        transferBox.Controls.Add(_transfer);
        Row("파일 전송", transferBox);
        Row("", new Label
        {
            Text = "상대 PC에서 복사(Ctrl+C)한 파일은 탐색기에서 우클릭 → (윈도우 11은 '더 많은 옵션 표시'\n또는 Shift+우클릭) → '" + Shared.ShellMenu.Text + "'로 가져와요.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });

        var layoutLabel = new Label { Text = "모니터를 책상 위 순서대로 끌어다 놓으세요", AutoSize = true, Margin = new Padding(0, 14, 0, 6) };
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Location = new Point(16, 16) };
        stack.Controls.Add(grid);
        // Host-only rows hide both halves; an all-hidden AutoSize row collapses to nothing.
        var extra = new List<Control> { addressLabel, addressRow, lockLabel, lockRow, quickLabel, quickRow, keyHint };
        if (layout is not null)
        {
            _canvas = new LayoutCanvas(layout, hostPcId)
            {
                Size = new Size(620, 260),
                Margin = new Padding(0, 0, 0, 4),
                EmptyHint = remoteConnected ? "" : "송출컴과 연결되면 송출컴 모니터도 여기에 나타나요.",
            };
            _canvas.Changed += UpdateState;
            stack.Controls.Add(layoutLabel);
            stack.Controls.Add(_canvas);
            extra.Add(layoutLabel);
            extra.Add(_canvas);
        }
        _hostOnly = extra.ToArray();
        stack.Controls.Add(_error);
        stack.Controls.Add(_update);

        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_save);
        stack.Controls.Add(buttons);
        Controls.Clear();
        Controls.Add(stack);
        buttons.MinimumSize = new Size(_canvas?.Width ?? 420, 0);

        AcceptButton = _save;
        CancelButton = cancel;
        _save.Click += (_, _) => TrySave();
        foreach (var c in new Control[] { _host, _receiver, _address, _password }) c.TextChanged += (_, _) => UpdateState();
        _host.CheckedChanged += (_, _) => { ApplyRole(); UpdateState(); };
        _lockKey.ValueChanged += UpdateState;
        _quickKey.ValueChanged += UpdateState;
        _lockKey.CapturingChanged += on => CapturingKeys?.Invoke(on);
        _quickKey.CapturingChanged += on => CapturingKeys?.Invoke(on);
        _fileLimit.ValueChanged += (_, _) => UpdateLive();
        _speedLimit.ValueChanged += (_, _) => UpdateLive();
        _live.Tick += (_, _) => UpdateLive();
        _live.Start();
        ApplyRole();
        UpdateState();
        UpdateLive();
        Shown += async (_, _) => await ShowUpdateAsync();
        FormClosed += (_, _) => _closing.Cancel();
    }

    /// <summary>New release on GitHub: a link above the buttons, and a popup once per release
    /// (only here, because the game PC screen is on stream).</summary>
    async Task ShowUpdateAsync()
    {
        var release = await Shared.UpdateCheck.LatestAsync(_closing.Token);
        if (IsDisposed || release is not { } r) return;
        var current = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "";
        if (!Core.Updates.ReleaseVersion.IsNewer(r.Tag, current)) return;

        void Open() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Url) { UseShellExecute = true });
        _update.Text = $"새 버전 {r.Tag}이 나왔어요 — 릴리즈 페이지 열기";
        _update.LinkClicked += (_, _) => Open();
        _update.Visible = true;

        var seenPath = Path.Combine(Path.GetDirectoryName(AppConfig.DefaultPath)!, "update-seen.txt");
        string? seen = null;
        try { seen = File.Exists(seenPath) ? File.ReadAllText(seenPath).Trim() : null; } catch { }
        if (seen == r.Tag) return;
        try { File.WriteAllText(seenPath, r.Tag); } catch { }
        if (MessageBox.Show(this, $"새 버전 {r.Tag}이 나왔어요.\n릴리즈 페이지를 열까요?", "Monitor Bridge", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            Open();
    }

    /// <summary>Limit mismatch and transfer progress; both change while the window is open.</summary>
    void UpdateLive()
    {
        int own = (int)_fileLimit.Value;
        if (_peerLimitMB?.Invoke() is { } peer && peer != own)
        {
            _limitWarn.Text = $"상대 PC 한도는 {peer:N0}MB예요. 작은 쪽({Math.Min(own, peer):N0}MB)까지만 보낼 수 있어요.";
            _limitWarn.Visible = true;
        }
        else _limitWarn.Visible = false;

        int ownSpeed = (int)_speedLimit.Value;
        if (_peerSpeedMBps?.Invoke() is { } peerSpeed && peerSpeed != ownSpeed)
        {
            _speedWarn.Text = $"상대 PC 설정은 {TransferProgress.SpeedText(peerSpeed)}, 실제 전송 속도는 "
                + $"{TransferProgress.SpeedText(TransferProgress.EffectiveSpeed(ownSpeed, peerSpeed))}예요.";
            _speedWarn.Visible = true;
        }
        else _speedWarn.Visible = false;

        var p = _progress?.Invoke();
        _bar.Visible = p is not null;
        if (p is not null)
        {
            int v = p.Total <= 0 ? 0 : (int)Math.Clamp(p.Done * 100 / p.Total, 0, 100);
            // Windows animates a bar only when it grows; stepping past and back shows the real value at once.
            if (v < 100) { _bar.Value = v + 1; _bar.Value = v; } else _bar.Value = 100;
        }
        _transfer.Text = p?.Describe() ?? "전송 중인 파일이 없어요";
        _transfer.ForeColor = p is null ? SystemColors.GrayText : p.State == TransferState.Failed ? Warn : SystemColors.ControlText;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _live.Dispose(); _closing.Dispose(); }
        base.Dispose(disposing);
    }

    void ApplyRole()
    {
        foreach (var c in _hostOnly) c.Visible = _host.Checked;
        // Only the stream PC receives input; the game PC works the same either way.
        _adminWarn.Visible = _receiver.Checked && !AutoStart.IsAdmin();
    }

    string? Problem()
    {
        if (string.IsNullOrWhiteSpace(_password.Text)) return "비밀번호를 입력해 주세요.";
        if (_host.Checked && string.IsNullOrWhiteSpace(_address.Text)) return "송출컴 IP를 입력해 주세요. 송출컴 설정 창 맨 아래에 나와 있어요.";
        // Both empty is fine: "not used" twice is not a clash.
        if (_host.Checked && _lockKey.Value.Length > 0 && _lockKey.Value == _quickKey.Value) return "가두기 키와 빠른 전환 키를 다르게 정해 주세요.";
        if (_host.Checked && _canvas?.HasOverlap == true) return "모니터가 겹쳐 있어요. 빨간 테두리 모니터를 옆으로 옮겨 주세요.";
        return null;
    }

    void UpdateState()
    {
        var p = Problem();
        _error.Text = p ?? "";
        _error.Visible = p is not null;
        _save.Enabled = p is null;
    }

    void TrySave()
    {
        if (Problem() is not null) return;
        Result = new AppConfig
        {
            Role = _host.Checked ? Role.Host : Role.Receiver,
            ReceiverAddress = _address.Text.Trim(),
            Port = (int)_port.Value,
            Password = _password.Text,
            LockHotkey = _lockKey.Value,
            QuickSwitchHotkey = _quickKey.Value,
            FileSizeLimitMB = (int)_fileLimit.Value,
            FileSpeedLimitMBps = (int)_speedLimit.Value,
            AutoStart = _autoStart.Checked,
            Placements = _canvas?.Placements.ToList() ?? _source.Placements,
        };
        DialogResult = DialogResult.OK;
    }

    /// <summary>Opens or closes the speed guide under the speed row.</summary>
    public void ToggleSpeedGuide()
    {
        // A flag, not _speedGuide.Visible: that reads false while the window itself is not shown yet.
        _guideOpen = !_guideOpen;
        _speedGuide.Visible = _guideOpen;
        _guideLink.Text = _guideOpen ? GuideOpen : GuideClosed;
    }

    public void ShowLocalAddresses(string text)
    {
        // Receiver: show this PC's IP so the user can type it on the game PC.
        var l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 10, 0, 0) };
        var stack = (FlowLayoutPanel)Controls[0];
        stack.Controls.Add(l);
        stack.Controls.SetChildIndex(l, stack.Controls.IndexOf(_error));
    }
}

/// <summary>Read-only box that records the next key combination pressed in it.</summary>
internal sealed class HotkeyBox : TextBox
{
    string _value = "";
    string _before = "";
    static readonly Color CaptureBack = ColorTranslator.FromHtml("#E3F2F1");
    public event Action? ValueChanged;
    /// <summary>True when the box starts waiting for a key (it has focus), false when it stops.</summary>
    public event Action<bool>? CapturingChanged;

    public HotkeyBox() { ReadOnly = true; Width = 160; BackColor = SystemColors.Window; ShortcutsEnabled = false; }

    /// <summary>"" means the key is not used.</summary>
    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            Text = value.Length == 0 ? "사용 안 함" : Pretty(value);
            ForeColor = value.Length == 0 ? SystemColors.GrayText : SystemColors.WindowText;
        }
    }

    public new void Clear()
    {
        Value = "";
        ValueChanged?.Invoke();
    }

    // Focus = capture mode: the box only waits for the new key, and the running hotkeys are paused meanwhile.
    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        _before = _value;
        Text = "새 키를 누르세요 (Esc: 취소)";
        ForeColor = SystemColors.GrayText;
        BackColor = CaptureBack;
        CapturingChanged?.Invoke(true);
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        BackColor = SystemColors.Window;
        Value = _value; // show the chosen key again
        CapturingChanged?.Invoke(false);
    }

    void EndCapture() => FindForm()?.SelectNextControl(this, true, true, true, true);

    static string Pretty(string s) => string.Join("+", s.Split('+').Select(p => p switch
    {
        "Oem3" => "`", "Oem5" => "\\", "OemMinus" => "-", "Oemplus" => "=", "Oemcomma" => ",", "OemPeriod" => ".",
        "Scroll" => "Scroll Lock",
        _ when p.Length == 2 && p[0] == 'D' && char.IsDigit(p[1]) => p[1..],
        _ => p,
    }));

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape) { Value = _before; EndCapture(); return true; }
        if (key is Keys.Back or Keys.Delete && (keyData & Keys.Modifiers) == 0) { Clear(); EndCapture(); return true; }
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin or Keys.Tab) return base.ProcessCmdKey(ref msg, keyData);
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add(key.ToString());
        var s = string.Join("+", parts);
        if (Hotkey.TryParse(s, out var h)) { Value = h.ToString(); ValueChanged?.Invoke(); EndCapture(); }
        else Text = $"{s} 키는 쓸 수 없어요";
        return true;
    }
}
