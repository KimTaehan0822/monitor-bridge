using MonitorBridge.Core.Files;
using MonitorBridge.Core.Protocol;

namespace MonitorBridge.UI;

/// <summary>"Bring these files here?" — a small box next to the cursor, shown after the right-click menu.</summary>
internal sealed class PasteConfirm : Form
{
    PasteConfirm(string peerName, FileListOffer offer, string folder)
    {
        Text = "Monitor Bridge";
        Font = new Font("맑은 고딕", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(16, 14, 16, 12);

        var name = offer.Count > 1 ? $"{offer.FirstName} 외 {offer.Count - 1}개" : offer.FirstName;
        // One thought per line, so Korean never wraps in the middle of a word.
        var question = new Label { Text = $"{peerName}에서 복사한 파일을 이 폴더로 가져올까요?", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        var what = new Label
        {
            Text = $"{name}  ({TransferProgress.Size(offer.TotalBytes)})",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 2),
        };
        var where = new Label
        {
            Text = folder,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 12),
        };
        var ok = new Button { Text = "가져오기", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.None, Margin = Padding.Empty };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Location = new Point(16, 14) };
        stack.Controls.Add(question);
        stack.Controls.Add(what);
        stack.Controls.Add(where);
        stack.Controls.Add(buttons);
        Controls.Add(stack);
        buttons.MinimumSize = new Size(new[] { question, what, where }.Max(l => l.PreferredSize.Width), 0);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Next to the cursor, but fully on its screen.
        var at = Cursor.Position;
        var area = Screen.FromPoint(at).WorkingArea;
        Location = new Point(Math.Clamp(at.X + 12, area.Left, area.Right - Width), Math.Clamp(at.Y + 12, area.Top, area.Bottom - Height));
    }

    /// <summary>UI thread. True when the person chose 가져오기.</summary>
    public static bool Ask(string peerName, FileListOffer offer, string folder)
    {
        using var f = new PasteConfirm(peerName, offer, folder);
        return f.ShowDialog() == DialogResult.OK;
    }

    /// <summary>For the offscreen render check only.</summary>
    public static Form ForRender(string peerName, FileListOffer offer, string folder) => new PasteConfirm(peerName, offer, folder);
}
