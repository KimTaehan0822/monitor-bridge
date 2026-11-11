using MonitorBridge.Core.Layout;

namespace MonitorBridge.Core.Tests;

public class VirtualLayoutTests
{
    // A(stream) | B(game, taller, raised 180px) | C(stream). Stream PC's own Windows layout has A at 0 and C at 1920.
    internal static readonly MonitorInfo A = new("stream", @"\\.\DISPLAY1", 1920, 1080, 0, 0, 1.0);
    internal static readonly MonitorInfo B = new("game", @"\\.\DISPLAY1", 2560, 1440, 0, 0, 1.25);
    internal static readonly MonitorInfo C = new("stream", @"\\.\DISPLAY2", 1920, 1080, 1920, 0, 1.0);

    internal static VirtualLayout Layout() => new(
        new[] { A, B, C },
        new[] { new Placement(A.Key, 0, 0), new Placement(B.Key, 1920, -180), new Placement(C.Key, 4480, 0) });

    [Fact]
    public void Move_WithinMonitor_AddsDelta() =>
        Assert.Equal(new VPoint(110, 95), Layout().Move(new VPoint(100, 100), 10, -5));

    [Fact]
    public void Move_CrossesRightEdgeIntoNeighbor()
    {
        var l = Layout();
        var p = l.Move(new VPoint(1915, 500), 10, 0);
        Assert.Equal(new VPoint(1925, 500), p);
        Assert.Equal("game", l.MonitorAt(p)!.Info.PcId);
    }

    [Fact]
    public void Move_CrossesBetweenDifferentSizes_KeepsVirtualY()
    {
        var l = Layout();
        var p = l.Move(new VPoint(4475, 1000), 10, 0);
        Assert.Equal(1000, p.Y);
        Assert.Equal(C.Key, l.MonitorAt(p)!.Info.Key);
    }

    [Fact]
    public void Move_NoNeighbor_StopsAtEdge() =>
        Assert.Equal(new VPoint(0, 500), Layout().Move(new VPoint(5, 500), -50, 0));

    [Fact]
    public void Move_EdgeWithoutOverlap_Stops() =>
        Assert.Equal(new VPoint(1920, -180), Layout().Move(new VPoint(1930, -180), -50, 0));

    [Fact]
    public void Move_LargeDeltaSpansTwoMonitors()
    {
        var l = Layout();
        var p = l.Move(new VPoint(1900, 500), 3000, 0);
        Assert.Equal(4900, p.X);
        Assert.Equal(C.Key, l.MonitorAt(p)!.Info.Key);
    }

    [Fact]
    public void ToOs_FromOs_RoundTrip()
    {
        var l = Layout();
        var (m, x, y) = l.ToOs(new VPoint(4500, 10));
        Assert.Equal(C.Key, m.Info.Key);
        Assert.Equal((1940, 10), (x, y));
        Assert.Equal(new VPoint(4500, 10), l.FromOs("stream", 1940, 10));
    }

    [Fact]
    public void Constructor_UnplacedMonitorAppendedRight()
    {
        var l = new VirtualLayout(new[] { A, B }, new[] { new Placement(A.Key, 0, 0) });
        var b = l.Monitors.Single(m => m.Info.Key == B.Key);
        Assert.Equal((1920, 0), (b.X, b.Y));
    }

    [Fact]
    public void Constructor_PlacementWithoutMonitorDropped()
    {
        var l = new VirtualLayout(new[] { A }, new[] { new Placement(A.Key, 0, 0), new Placement("gone/X", 5, 5) });
        Assert.Single(l.ToPlacements());
    }

    [Fact]
    public void Snap_WithinThreshold_AlignsEdges()
    {
        var others = new[] { new PlacedMonitor(A, 0, 0) };
        Assert.Equal((1920, 0), VirtualLayout.Snap(1935, 10, 100, 100, others));
        Assert.Equal((1945, 30), VirtualLayout.Snap(1945, 30, 100, 100, others));
    }
}
