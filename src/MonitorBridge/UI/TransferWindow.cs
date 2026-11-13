using MonitorBridge.Core.Files;

namespace MonitorBridge.UI;

/// <summary>
/// The file transfer window: what is moving, how far, and a cancel button. Opened only by the person (tray click
/// or the start notice) so it never appears on the broadcast by itself.
/// </summary>
internal sealed class TransferWindow : Form
{
    static TransferWindow? _open;
    static readonly Color Accent = ColorTranslator.FromHtml("#1F8A84");
    static readonly Color Warn = ColorTranslator.FromHtml("#C0392B");

    readonly Func<TransferProgress?> _progress;
    readonly Func<bool> _cancel;
    readonly Label _name = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 6) };
    readonly ProgressBar _bar = new() { Width = 380, Height = 10, Style = ProgressBarStyle.Continuous, Maximum = 100, Margin = new Padding(0, 0, 0, 6) };
    readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 12) };
    readonly Button _cancelButton = new() { Text = "전송 취소", AutoSize = true };
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    TransferWindow(Func<TransferProgress?> progress, Func<bool> cancel)
    {
        _progress = progress;
        _cancel = cancel;
        Text = "Monitor Bridge - 파일 전송";
        Font = new Font("맑은 고딕", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(16, 14, 16, 12);
        _name.Font = new Font(Font, FontStyle.Bold);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, MinimumSize = new Size(380, 0), Margin = Padding.Empty };
        buttons.Controls.Add(_cancelButton);
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Location = new Point(16, 14) };
        stack.Controls.Add(_name);
        stack.Controls.Add(_bar);
        stack.Controls.Add(_status);
        stack.Controls.Add(buttons);
        Controls.Add(stack);

        _cancelButton.Click += (_, _) => { _cancel(); Refresh(); };
        _tick.Tick += (_, _) => UpdateView();
        _tick.Start();
        UpdateView();
    }

    void UpdateView()
    {
        var p = _progress();
        bool running = p?.State == TransferState.Running;
        _name.Text = p is null ? "전송 중인 파일이 없어요" : (p.Sending ? $"{p.PeerName}으로 보내는 중: " : $"{p.PeerName}에서 받는 중: ") + p.Name;
        if (p is not null && !running) _name.Text = p.Name;
        _bar.Visible = p is not null;
        if (p is not null)
        {
            int v = p.State == TransferState.Done ? 100 : p.Total <= 0 ? 0 : (int)Math.Clamp(p.Done * 100 / p.Total, 0, 100);
            // Windows animates a bar only when it grows; stepping past and back shows the real value at once.
            if (v < 100) { _bar.Value = v + 1; _bar.Value = v; } else _bar.Value = 100;
        }
        _status.Text = p?.Describe() ?? "상대 PC에서 파일을 복사한 뒤, 탐색기에서 우클릭해 가져오세요.";
        _status.ForeColor = p?.State is TransferState.Failed or TransferState.Cancelled ? Warn
            : p?.State == TransferState.Done ? Accent : SystemColors.ControlText;
        _cancelButton.Enabled = running;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromPoint(Cursor.Position).WorkingArea; // near the tray, on the screen in use
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Dispose();
        if (ReferenceEquals(_open, this)) _open = null;
        base.OnFormClosed(e);
    }

    /// <summary>UI thread. Opens the window, or brings the open one forward.</summary>
    public static void ShowOrFocus(Func<TransferProgress?> progress, Func<bool> cancel)
    {
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        _open = new TransferWindow(progress, cancel);
        _open.Show();
    }

    /// <summary>For the offscreen render check only.</summary>
    public static Form ForRender(Func<TransferProgress?> progress) => new TransferWindow(progress, () => false);
}
