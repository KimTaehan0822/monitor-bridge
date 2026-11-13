namespace MonitorBridge.UI;

/// <summary>A short expanding ring at a screen point, so the cursor is easy to find. Click-through, never takes focus.</summary>
internal sealed class RippleWindow : Form
{
    // shortcut: fixed physical size, not scaled per monitor DPI; scale it if it looks small on high-DPI screens.
    const int Box = 160, DurationMs = 450;
    static readonly Color Ring = ColorTranslator.FromHtml("#1F8A84");
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    readonly long _start = Environment.TickCount64;
    readonly int _cx, _cy;

    /// <summary>UI thread only.</summary>
    public static void Play(int osX, int osY) => new RippleWindow(osX, osY).Show();

    RippleWindow(int x, int y)
    {
        _cx = x;
        _cy = y;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = TransparencyKey = Color.Magenta;
        DoubleBuffered = true;
        Bounds = new Rectangle(x - Box / 2, y - Box / 2, Box, Box);
        _timer.Tick += (_, _) =>
        {
            if (Progress >= 1) { Close(); return; }
            Opacity = 1 - Progress;
            Invalidate();
        };
        _timer.Start();
    }

    double Progress => Math.Min(1, (Environment.TickCount64 - _start) / (double)DurationMs);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // layered | click-through | no taskbar/alt-tab | never activated | topmost
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Windows may resize the window for the target monitor's DPI; keep it centred on the cursor.
        Location = new Point(_cx - Width / 2, _cy - Height / 2);
    }

    protected override void OnPaint(PaintEventArgs e) => Draw(e.Graphics, ClientSize.Width, ClientSize.Height, Progress);

    public static void Draw(Graphics g, int w, int h, double progress)
    {
        float ease = (float)(1 - Math.Pow(1 - progress, 3));
        float max = Math.Min(w, h) / 2f - 6;
        float r = 10 + (max - 10) * ease;
        float cx = w / 2f, cy = h / 2f;
        // No anti-aliasing: blended edge pixels would show the magenta key colour.
        using var outline = new Pen(Color.White, 8);
        using var ring = new Pen(Ring, 4);
        g.DrawEllipse(outline, cx - r, cy - r, r * 2, r * 2);
        g.DrawEllipse(ring, cx - r, cy - r, r * 2, r * 2);
    }

    /// <summary>Check without showing anything on screen: frames at 0/33/66/90% on dark and light backgrounds.</summary>
    public static void RenderFrames(string png)
    {
        double[] at = { 0, 0.33, 0.66, 0.9 };
        using var bmp = new Bitmap(Box * at.Length, Box * 2);
        using var g = Graphics.FromImage(bmp);
        for (int row = 0; row < 2; row++)
            for (int i = 0; i < at.Length; i++)
            {
                var bg = row == 0 ? Color.FromArgb(30, 30, 30) : Color.FromArgb(235, 235, 235);
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, i * Box, row * Box, Box, Box);
                var state = g.Save();
                g.TranslateTransform(i * Box, row * Box);
                Draw(g, Box, Box, at[i]);
                g.Restore(state);
            }
        bmp.Save(png);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
