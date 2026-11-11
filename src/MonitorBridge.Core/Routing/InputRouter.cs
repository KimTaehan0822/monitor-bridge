using MonitorBridge.Core.Layout;
using MonitorBridge.Core.Protocol;

namespace MonitorBridge.Core.Routing;

public abstract record LocalAction;
/// <summary>Hide the host cursor and hold it at this OS point while input goes to the remote PC.</summary>
public sealed record ParkAndHide(int OsX, int OsY) : LocalAction;
public sealed record UnparkAndShow(int OsX, int OsY) : LocalAction;
/// <summary>Move the host cursor (host monitors whose Windows layout disagrees with the virtual layout).</summary>
public sealed record Warp(int OsX, int OsY) : LocalAction;
public sealed record ReleaseLocalKeys((ushort Scan, bool Extended)[] Keys, MouseButtonKind[] Buttons) : LocalAction;

public sealed record RouterResult(bool Block, IReadOnlyList<Message> ToRemote, IReadOnlyList<LocalAction> Local)
{
    public static readonly RouterResult Pass = new(false, Array.Empty<Message>(), Array.Empty<LocalAction>());
    public static readonly RouterResult Swallow = new(true, Array.Empty<Message>(), Array.Empty<LocalAction>());
    public static RouterResult Forward(Message m) => new(true, new[] { m }, Array.Empty<LocalAction>());
}

/// <summary>
/// Decides, for every host input event, whether it stays on the host or goes to the remote PC.
/// The cursor position lives on the virtual layout; PCs' own Windows layouts are ignored.
/// Not thread-safe: call from the hook thread only.
/// </summary>
public sealed class InputRouter
{
    readonly string _hostPcId;
    VirtualLayout _layout;
    (int X, int Y) _prevOs;
    (int X, int Y) _park;
    bool _ignoreNextPush; // our own SetCursorPos can echo back as a same-point move
    readonly Dictionary<string, VPoint> _last = new();
    readonly HashSet<(ushort, bool)> _heldKeys = new();
    readonly HashSet<MouseButtonKind> _heldButtons = new();

    public string ActivePcId { get; private set; }
    public VPoint Position { get; private set; }
    public bool IsRemote => ActivePcId != _hostPcId;

    public InputRouter(VirtualLayout layout, string hostPcId, VPoint startOs)
    {
        _layout = layout;
        _hostPcId = ActivePcId = hostPcId;
        _prevOs = (startOs.X, startOs.Y);
        Position = layout.FromOs(hostPcId, startOs.X, startOs.Y) ?? layout.Move(new VPoint(startOs.X, startOs.Y), 0, 0);
    }

    public RouterResult OnMouseMove(int x, int y) => IsRemote ? RemoteMove(x, y) : HostMove(x, y);

    RouterResult HostMove(int x, int y)
    {
        var prev = _prevOs;
        _prevOs = (x, y);
        var vPrev = _layout.FromOs(_hostPcId, prev.X, prev.Y) ?? Position;
        var prevMon = _layout.MonitorAt(vPrev);
        var v = _layout.FromOs(_hostPcId, x, y);
        int dx = x - prev.X, dy = y - prev.Y;

        bool samePoint = dx == 0 && dy == 0;
        bool ignorePush = _ignoreNextPush;
        _ignoreNextPush = false;
        if (v is { } inside)
        {
            var mon = _layout.MonitorAt(inside);
            if (mon == prevMon && !(samePoint && !ignorePush && prevMon is not null && PushDirection(prevMon, vPrev) is not null))
            {
                Position = inside;
                return RouterResult.Pass;
            }
            if (samePoint)
            {
                // Windows clamped the cursor at the edge: treat a repeated point as a push outward.
                var (px, py) = PushDirection(prevMon!, vPrev)!.Value;
                (dx, dy) = (px, py);
            }
        }

        var target = _layout.Move(vPrev, dx, dy);
        var targetMon = _layout.MonitorAt(target)!;
        if (targetMon.Info.PcId != _hostPcId) return EnterRemote(target, vPrev, prevMon);
        Position = target;
        if (v is { } got && _layout.MonitorAt(got) == targetMon) { Position = got; return RouterResult.Pass; }
        if (targetMon == prevMon && v is null) return RouterResult.Pass; // pushing an edge with no neighbour
        var (_, ox, oy) = _layout.ToOs(target);
        _prevOs = (ox, oy);
        _ignoreNextPush = true;
        return new RouterResult(true, Array.Empty<Message>(), new LocalAction[] { new Warp(ox, oy) });
    }

    static (int, int)? PushDirection(PlacedMonitor m, VPoint p)
    {
        if (p.X == m.X) return (-1, 0);
        if (p.X == m.Right - 1) return (1, 0);
        if (p.Y == m.Y) return (0, -1);
        if (p.Y == m.Bottom - 1) return (0, 1);
        return null;
    }

    RouterResult EnterRemote(VPoint target, VPoint hostLast, PlacedMonitor? hostMon)
    {
        _last[_hostPcId] = hostLast;
        var release = TakeHeld();
        var m = hostMon ?? _layout.Monitors.First(pm => pm.Info.PcId == _hostPcId);
        _park = (m.Info.OsX + m.Info.Width / 2, m.Info.OsY + m.Info.Height / 2);
        var (tm, ox, oy) = _layout.ToOs(target);
        ActivePcId = tm.Info.PcId;
        Position = target;
        return new RouterResult(true, new Message[] { new CursorEnter(ox, oy) },
            new LocalAction[] { release, new ParkAndHide(_park.X, _park.Y) });
    }

    RouterResult RemoteMove(int x, int y)
    {
        int dx = x - _park.X, dy = y - _park.Y;
        if (dx == 0 && dy == 0) return RouterResult.Swallow;
        var target = _layout.Move(Position, dx, dy);
        var (tm, ox, oy) = _layout.ToOs(target);
        if (tm.Info.PcId == _hostPcId) return LeaveRemote(target, notifyRemote: true);
        // shortcut: one remote PC only; a second remote PC's monitor is treated as the same peer. Upgrade with multi-PC support.
        Position = target;
        return RouterResult.Forward(new CursorMove(ox, oy));
    }

    RouterResult LeaveRemote(VPoint hostTarget, bool notifyRemote)
    {
        _last[ActivePcId] = Position;
        TakeHeld(); // remote side releases via ReleaseAll
        ActivePcId = _hostPcId;
        Position = hostTarget;
        var (_, ox, oy) = _layout.ToOs(hostTarget);
        _prevOs = (ox, oy);
        _ignoreNextPush = true;
        var toRemote = notifyRemote ? new Message[] { new ReleaseAll(), new CursorLeave() } : Array.Empty<Message>();
        return new RouterResult(true, toRemote, new LocalAction[] { new UnparkAndShow(ox, oy) });
    }

    ReleaseLocalKeys TakeHeld()
    {
        var r = new ReleaseLocalKeys(_heldKeys.ToArray(), _heldButtons.ToArray());
        _heldKeys.Clear();
        _heldButtons.Clear();
        return r;
    }

    public RouterResult OnButton(MouseButtonKind b, bool down)
    {
        if (down) _heldButtons.Add(b);
        else if (!_heldButtons.Remove(b)) return IsRemote ? RouterResult.Swallow : RouterResult.Pass;
        return IsRemote ? RouterResult.Forward(new MouseButton(b, down)) : RouterResult.Pass;
    }

    public RouterResult OnWheel(bool horizontal, int delta) =>
        IsRemote ? RouterResult.Forward(new MouseWheel(horizontal, delta)) : RouterResult.Pass;

    public RouterResult OnKey(ushort scan, bool extended, bool down)
    {
        if (down) _heldKeys.Add((scan, extended));
        else if (!_heldKeys.Remove((scan, extended))) return IsRemote ? RouterResult.Swallow : RouterResult.Pass;
        return IsRemote ? RouterResult.Forward(new Key(scan, extended, down)) : RouterResult.Pass;
    }

    public RouterResult QuickSwitch()
    {
        if (IsRemote) return LeaveRemote(LastOrCenter(_hostPcId), notifyRemote: true);
        var remote = _layout.Monitors.FirstOrDefault(m => m.Info.PcId != _hostPcId);
        if (remote is null) return RouterResult.Pass;
        var hostMon = _layout.MonitorAt(Position);
        return EnterRemote(LastOrCenter(remote.Info.PcId), Position, hostMon);
    }

    VPoint LastOrCenter(string pcId) =>
        _last.TryGetValue(pcId, out var p) && _layout.MonitorAt(p)?.Info.PcId == pcId
            ? p
            : _layout.Monitors.First(m => m.Info.PcId == pcId).Center;

    /// <summary>Returns the cursor to the host and drops remote monitors until the next layout arrives.</summary>
    public RouterResult OnDisconnected()
    {
        var res = IsRemote ? LeaveRemote(LastOrCenter(_hostPcId), notifyRemote: false) : RouterResult.Pass;
        var hostOnly = _layout.Monitors.Where(m => m.Info.PcId == _hostPcId).ToList();
        _layout = new VirtualLayout(hostOnly.Select(m => m.Info), hostOnly.Select(m => new Placement(m.Info.Key, m.X, m.Y)));
        return res;
    }

    public RouterResult OnLayoutChanged(VirtualLayout layout)
    {
        _layout = layout;
        if (IsRemote && _layout.MonitorAt(Position)?.Info.PcId != ActivePcId)
        {
            _last.Remove(ActivePcId);
            return LeaveRemote(LastOrCenter(_hostPcId), notifyRemote: true);
        }
        if (!IsRemote) Position = _layout.FromOs(_hostPcId, _prevOs.X, _prevOs.Y) ?? Position;
        return RouterResult.Pass;
    }
}
