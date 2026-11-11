using System.Drawing.Drawing2D;
using MonitorBridge.Core.Layout;

namespace MonitorBridge.UI;

/// <summary>Drag-and-drop editor for the virtual layout. Monitors are drawn to scale, coloured per PC.</summary>
internal sealed class LayoutCanvas : Control
{
    static readonly Color HostColor = ColorTranslator.FromHtml("#1F8A84");
    static readonly Color RemoteColor = ColorTranslator.FromHtml("#4F5BD5");
    static readonly Color OverlapColor = ColorTranslator.FromHtml("#C0392B");

    sealed class Item
    {
        public required MonitorInfo Info;
        public required string Label;
        public int X, Y;
        public Rectangle Virtual => new(X, Y, Info.Width, Info.Height);
    }

    readonly List<Item> _items = new();
    readonly string _hostPcId;
    Item? _drag;
    Point _dragStart;
    int _startX, _startY;
    float _scale = 1;
    PointF _origin;

    public event Action? Changed;
    public string EmptyHint { get; set; } = "";

    public LayoutCanvas(VirtualLayout layout, string hostPcId)
    {
        _hostPcId = hostPcId;
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        foreach (var group in layout.Monitors.GroupBy(m => m.Info.PcId))
        {
            int n = 1;
            foreach (var m in group.OrderBy(m => m.Info.OsX).ThenBy(m => m.Info.OsY))
                _items.Add(new Item { Info = m.Info, Label = $"{(m.Info.PcId == hostPcId ? "게임컴" : "송출컴")} {n++}", X = m.X, Y = m.Y });
        }
        Fit();
    }

    public IEnumerable<Placement> Placements => _items.Select(i => new Placement(i.Info.Key, i.X, i.Y));

    public bool HasOverlap => _items.Any(a => _items.Any(b => a != b && a.Virtual.IntersectsWith(b.Virtual)));

    bool Overlaps(Item a) => _items.Any(b => a != b && a.Virtual.IntersectsWith(b.Virtual));

    void Fit()
    {
        if (_items.Count == 0 || Width < 10 || Height < 10) return;
        int minX = _items.Min(i => i.X), minY = _items.Min(i => i.Y);
        int maxX = _items.Max(i => i.X + i.Info.Width), maxY = _items.Max(i => i.Y + i.Info.Height);
        // Leave room around the monitors so one can be dragged to any side.
        float pad = 0.35f;
        float w = (maxX - minX) * (1 + 2 * pad), h = (maxY - minY) * (1 + 2 * pad);
        _scale = Math.Min(Width / w, Height / h);
        _origin = new PointF(
            Width / 2f - (minX + (maxX - minX) / 2f) * _scale,
            Height / 2f - (minY + (maxY - minY) / 2f) * _scale);
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (_drag is null) Fit(); }

    RectangleF Screen(Item i) => new(_origin.X + i.X * _scale, _origin.Y + i.Y * _scale, i.Info.Width * _scale, i.Info.Height * _scale);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(SystemColors.Window);
        using var border = new Pen(SystemColors.ControlDark);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        if (_items.Count == 0 || !string.IsNullOrEmpty(EmptyHint))
        {
            TextRenderer.DrawText(g, EmptyHint, Font, new Rectangle(8, Height - 28, Width - 16, 22), SystemColors.GrayText, TextFormatFlags.Left);
        }
        foreach (var i in _items.OrderBy(i => i == _drag))
        {
            var r = Screen(i);
            var baseColor = i.Info.PcId == _hostPcId ? HostColor : RemoteColor;
            bool bad = Overlaps(i);
            using var fill = new SolidBrush(Color.FromArgb(i == _drag ? 235 : 210, baseColor));
            using var path = Rounded(r, 4);
            g.FillPath(fill, path);
            using var edge = new Pen(bad ? OverlapColor : Color.FromArgb(120, Color.Black), bad ? 3 : 1);
            g.DrawPath(edge, path);
            var rect = Rectangle.Round(r);
            using var big = new Font(Font.FontFamily, Math.Max(8, Font.Size * 1.25f), FontStyle.Bold);
            TextRenderer.DrawText(g, i.Label, big, new Rectangle(rect.X, rect.Y, rect.Width, rect.Height / 2 + 6),
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, $"{i.Info.Width}×{i.Info.Height}", Font, new Rectangle(rect.X, rect.Y + rect.Height / 2 + 6, rect.Width, rect.Height / 2),
                Color.FromArgb(225, Color.White), TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _drag = _items.LastOrDefault(i => Screen(i).Contains(e.Location));
        if (_drag is null) return;
        _dragStart = e.Location;
        (_startX, _startY) = (_drag.X, _drag.Y);
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = _drag is not null || _items.Any(i => Screen(i).Contains(e.Location)) ? Cursors.SizeAll : Cursors.Default;
        if (_drag is null) return;
        int x = _startX + (int)Math.Round((e.X - _dragStart.X) / _scale);
        int y = _startY + (int)Math.Round((e.Y - _dragStart.Y) / _scale);
        var others = _items.Where(i => i != _drag).Select(i => new PlacedMonitor(i.Info, i.X, i.Y));
        // Snap distance is ~8 screen pixels whatever the zoom.
        (_drag.X, _drag.Y) = VirtualLayout.Snap(x, y, _drag.Info.Width, _drag.Info.Height, others, (int)(8 / _scale));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag is null) return;
        _drag = null;
        Capture = false;
        Invalidate();
        Changed?.Invoke();
    }
}
