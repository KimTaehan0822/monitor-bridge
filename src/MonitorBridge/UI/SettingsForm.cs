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
    readonly TextBox _folder = new() { Width = 300 };
    readonly Button _browse = new() { Text = "찾아보기…", AutoSize = true };
    readonly CheckBox _autoStart = new() { Text = "윈도우 로그인 시 관리자 권한으로 자동 실행", AutoSize = true };
    static readonly Color Warn = ColorTranslator.FromHtml("#C0392B");
    readonly Label _error = new() { AutoSize = true, ForeColor = Warn };
    readonly Label _limitWarn = new() { AutoSize = true, ForeColor = Warn, Visible = false };
    readonly ProgressBar _bar = new() { Width = 300, Height = 8, Style = ProgressBarStyle.Continuous, Maximum = 100 };
    readonly Label _transfer = new() { AutoSize = true };
    readonly System.Windows.Forms.Timer _live = new() { Interval = 1000 };
    readonly Func<int?>? _peerLimitMB;
    readonly Func<TransferProgress?>? _progress;
    readonly Button _save = new() { Text = "저장", AutoSize = true };
    readonly LayoutCanvas? _canvas;
    readonly Control[] _hostOnly;
    readonly AppConfig _source;

    public AppConfig? Result { get; private set; }

    public SettingsForm(AppConfig cfg, VirtualLayout? layout, string hostPcId, bool remoteConnected,
        Func<int?>? peerLimitMB = null, Func<TransferProgress?>? progress = null)
    {
        _source = cfg;
        _peerLimitMB = peerLimitMB;
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
        _autoStart.Checked = cfg.AutoStart;
        _folder.Text = Shared.ReceiveFolders.Resolve(cfg);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
            c.Margin = new Padding(0, 3, 0, 3);
            grid.Controls.Add(c);
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
        Row("송출컴 IP", addressRow);
        Row("비밀번호", Inline(_password, Unit("두 PC에 같은 비밀번호를 넣으세요")));
        var lockRow = Inline(_lockKey, Unit("커서를 지금 있는 PC에 가둡니다"));
        Row("가두기 키", lockRow);
        var quickRow = Inline(_quickKey, Unit("다른 PC로 바로 넘어갑니다"));
        Row("빠른 전환 키", quickRow);
        Row("받은 파일 저장 폴더", Inline(_folder, _browse));
        Row("", new Label { Text = "상대 PC에서 복사한 파일은 이 폴더에 저장돼요.", AutoSize = true, ForeColor = SystemColors.GrayText });
        Row("파일 복사 한도", Inline(_fileLimit, Unit($"MB까지 상대 PC로 보냅니다 (최대 {MaxFileLimitMB:N0}MB)")));
        Row("", _limitWarn);
        Row("", _autoStart);
        var transferBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        _bar.Margin = new Padding(0, 4, 0, 4);
        _transfer.Margin = Padding.Empty;
        transferBox.Controls.Add(_bar);
        transferBox.Controls.Add(_transfer);
        Row("파일 전송", transferBox);

        var layoutLabel = new Label { Text = "모니터를 책상 위 순서대로 끌어다 놓으세요", AutoSize = true, Margin = new Padding(0, 14, 0, 6) };
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Location = new Point(16, 16) };
        stack.Controls.Add(grid);
        var extra = new List<Control> { addressRow, lockRow, quickRow };
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
        _fileLimit.ValueChanged += (_, _) => UpdateLive();
        _folder.TextChanged += (_, _) => UpdateState();
        _browse.Click += (_, _) => BrowseFolder();
        _live.Tick += (_, _) => UpdateLive();
        _live.Start();
        ApplyRole();
        UpdateState();
        UpdateLive();
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
        if (disposing) _live.Dispose();
        base.Dispose(disposing);
    }

    void ApplyRole()
    {
        foreach (var c in _hostOnly) c.Visible = _host.Checked;
    }

    string? Problem()
    {
        if (string.IsNullOrWhiteSpace(_password.Text)) return "비밀번호를 입력해 주세요.";
        if (_host.Checked && string.IsNullOrWhiteSpace(_address.Text)) return "송출컴 IP를 입력해 주세요. 송출컴 설정 창 맨 아래에 나와 있어요.";
        if (_host.Checked && _lockKey.Value == _quickKey.Value) return "가두기 키와 빠른 전환 키를 다르게 정해 주세요.";
        if (FolderProblem(_folder.Text.Trim()) is { } fp) return fp;
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

    void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "받은 파일을 저장할 폴더를 고르세요",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folder.Text.Trim()) ? _folder.Text.Trim() : Shared.ReceiveFolders.Downloads(),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
    }

    /// <summary>Checked while typing, so it never creates folders; creating happens once, on save.</summary>
    static string? FolderProblem(string path)
    {
        if (path.Length == 0) return "받은 파일 저장 폴더를 정해 주세요.";
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return @"받은 파일 저장 폴더는 C:\Users\…\Downloads 처럼 전체 경로로 적어 주세요.";
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return $"받은 파일 저장 폴더의 드라이브({root})를 찾을 수 없어요.";
        return null;
    }

    void TrySave()
    {
        if (Problem() is not null) return;
        var folder = Path.TrimEndingDirectorySeparator(_folder.Text.Trim());
        try { Directory.CreateDirectory(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _error.Text = "받은 파일 저장 폴더를 쓸 수 없어요: " + e.Message;
            _error.Visible = true;
            return;
        }
        // The default stays "" so it keeps following Downloads if the user moves that folder later.
        bool isDownloads = string.Equals(folder, Path.TrimEndingDirectorySeparator(Shared.ReceiveFolders.Downloads()), StringComparison.OrdinalIgnoreCase);
        Result = new AppConfig
        {
            Role = _host.Checked ? Role.Host : Role.Receiver,
            ReceiverAddress = _address.Text.Trim(),
            Port = (int)_port.Value,
            Password = _password.Text,
            LockHotkey = _lockKey.Value,
            QuickSwitchHotkey = _quickKey.Value,
            FileSizeLimitMB = (int)_fileLimit.Value,
            AutoStart = _autoStart.Checked,
            ReceiveFolder = isDownloads ? "" : folder,
            Placements = _canvas?.Placements.ToList() ?? _source.Placements,
        };
        DialogResult = DialogResult.OK;
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
    public event Action? ValueChanged;

    public HotkeyBox() { ReadOnly = true; Width = 160; BackColor = SystemColors.Window; ShortcutsEnabled = false; }

    public string Value
    {
        get => _value;
        set { _value = value; Text = Pretty(value); }
    }

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
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin or Keys.Tab) return base.ProcessCmdKey(ref msg, keyData);
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add(key.ToString());
        var s = string.Join("+", parts);
        if (Hotkey.TryParse(s, out var h)) { Value = h.ToString(); ValueChanged?.Invoke(); }
        else Text = $"{s} 키는 쓸 수 없어요";
        return true;
    }
}
