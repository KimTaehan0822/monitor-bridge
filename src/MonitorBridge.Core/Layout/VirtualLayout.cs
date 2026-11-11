namespace MonitorBridge.Core.Layout;

/// <summary>
/// The shared virtual desktop. Cursor movement follows this layout, never the PCs' own Windows display layouts.
/// </summary>
public sealed class VirtualLayout
{
    public IReadOnlyList<PlacedMonitor> Monitors { get; }

    public VirtualLayout(IEnumerable<MonitorInfo> monitors, IEnumerable<Placement> placements)
    {
        var byKey = placements.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last());
        var list = new List<PlacedMonitor>();
        var unplaced = new List<MonitorInfo>();
        foreach (var m in monitors)
        {
            if (byKey.TryGetValue(m.Key, out var p)) list.Add(new PlacedMonitor(m, p.X, p.Y));
            else unplaced.Add(m);
        }
        foreach (var m in unplaced)
        {
            int x = list.Count == 0 ? 0 : list.Max(pm => pm.Right);
            list.Add(new PlacedMonitor(m, x, 0));
        }
        Monitors = list;
    }

    public PlacedMonitor? MonitorAt(VPoint p) => Monitors.FirstOrDefault(m => m.Contains(p));

    public PlacedMonitor? Neighbor(VPoint edgePoint, int dirX, int dirY) =>
        MonitorAt(new VPoint(edgePoint.X + dirX, edgePoint.Y + dirY));

    public VPoint Move(VPoint from, int dx, int dy)
    {
        var p = ClampToNearest(from);
        p = MoveAxis(p, dx, horizontal: true);
        return MoveAxis(p, dy, horizontal: false);
    }

    private VPoint MoveAxis(VPoint p, int d, bool horizontal)
    {
        while (d != 0)
        {
            var m = MonitorAt(p)!;
            int dir = Math.Sign(d);
            int pos = horizontal ? p.X : p.Y;
            int edge = dir > 0 ? (horizontal ? m.Right : m.Bottom) - 1 : (horizontal ? m.X : m.Y);
            int step = dir > 0 ? Math.Min(d, edge - pos) : Math.Max(d, edge - pos);
            pos += step;
            d -= step;
            p = horizontal ? p with { X = pos } : p with { Y = pos };
            if (d == 0) break;
            if (Neighbor(p, horizontal ? dir : 0, horizontal ? 0 : dir) is null) break;
            p = horizontal ? p with { X = p.X + dir } : p with { Y = p.Y + dir };
            d -= dir;
        }
        return p;
    }

    private VPoint ClampToNearest(VPoint p)
    {
        if (MonitorAt(p) is not null) return p;
        VPoint best = p;
        long bestDist = long.MaxValue;
        foreach (var m in Monitors)
        {
            var c = new VPoint(Math.Clamp(p.X, m.X, m.Right - 1), Math.Clamp(p.Y, m.Y, m.Bottom - 1));
            long dist = (long)(c.X - p.X) * (c.X - p.X) + (long)(c.Y - p.Y) * (c.Y - p.Y);
            if (dist < bestDist) { bestDist = dist; best = c; }
        }
        return best;
    }

    public (PlacedMonitor Monitor, int OsX, int OsY) ToOs(VPoint p)
    {
        var c = ClampToNearest(p);
        var m = MonitorAt(c)!;
        return (m, m.Info.OsX + (c.X - m.X), m.Info.OsY + (c.Y - m.Y));
    }

    public VPoint? FromOs(string pcId, int osX, int osY)
    {
        foreach (var m in Monitors)
        {
            var i = m.Info;
            if (i.PcId == pcId && osX >= i.OsX && osX < i.OsX + i.Width && osY >= i.OsY && osY < i.OsY + i.Height)
                return new VPoint(m.X + (osX - i.OsX), m.Y + (osY - i.OsY));
        }
        return null;
    }

    /// <summary>Pulls a dragged rectangle onto nearby edges of other monitors (layout editor).</summary>
    public static (int X, int Y) Snap(int x, int y, int w, int h, IEnumerable<PlacedMonitor> others, int threshold = 20)
    {
        int bestX = x, bestY = y, dX = threshold + 1, dY = threshold + 1;
        foreach (var o in others)
        {
            foreach (var cx in new[] { o.Right, o.X - w, o.X, o.Right - w })
                if (Math.Abs(cx - x) < dX) { dX = Math.Abs(cx - x); bestX = cx; }
            foreach (var cy in new[] { o.Bottom, o.Y - h, o.Y, o.Bottom - h })
                if (Math.Abs(cy - y) < dY) { dY = Math.Abs(cy - y); bestY = cy; }
        }
        return (dX <= threshold ? bestX : x, dY <= threshold ? bestY : y);
    }

    public IEnumerable<Placement> ToPlacements() => Monitors.Select(m => new Placement(m.Info.Key, m.X, m.Y));
}
