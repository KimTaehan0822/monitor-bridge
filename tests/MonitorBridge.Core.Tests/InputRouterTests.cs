using MonitorBridge.Core.Layout;
using MonitorBridge.Core.Protocol;
using MonitorBridge.Core.Routing;

namespace MonitorBridge.Core.Tests;

// Layout A(stream) | B(game, host, OS 0,0 2560x1440, virtual 1920,-180) | C(stream).
public class InputRouterTests
{
    static InputRouter NewRouter() => new(VirtualLayoutTests.Layout(), "game", new VPoint(100, 100));

    /// <summary>Walks to the left edge and pushes once; cursor enters A at virtual (1919,320).</summary>
    static (InputRouter R, RouterResult Cross) CrossToA()
    {
        var r = NewRouter();
        r.OnMouseMove(0, 500);
        return (r, r.OnMouseMove(-1, 500));
    }

    [Fact]
    public void MoveInsideHost_Passes()
    {
        var res = NewRouter().OnMouseMove(200, 300);
        Assert.False(res.Block);
        Assert.Empty(res.ToRemote);
        Assert.Empty(res.Local);
    }

    [Fact]
    public void PushPastLeftEdge_SwitchesToStream()
    {
        var (r, res) = CrossToA();
        Assert.True(res.Block);
        Assert.Equal("stream", r.ActivePcId);
        Assert.Equal(new CursorEnter(1919, 320), Assert.Single(res.ToRemote));
        Assert.Contains(new ParkAndHide(1280, 720), res.Local);
    }

    [Fact]
    public void PushAtEdgeClamped_SwitchesOnThirdRepeat()
    {
        var r = NewRouter();
        Assert.False(r.OnMouseMove(0, 500).Block);
        Assert.False(r.OnMouseMove(0, 500).Block);
        Assert.False(r.OnMouseMove(0, 500).Block);
        Assert.Equal("game", r.ActivePcId);
        var res = r.OnMouseMove(0, 500);
        Assert.Equal("stream", r.ActivePcId);
        Assert.Equal(new CursorEnter(1919, 320), Assert.Single(res.ToRemote));
    }

    [Fact]
    public void SubPixelJitterAtEdge_DoesNotCross()
    {
        // Same-point events broken up by real movement never add up to a push.
        var r = NewRouter();
        for (int i = 0; i < 10; i++)
        {
            r.OnMouseMove(0, 500);
            r.OnMouseMove(0, 500);
            r.OnMouseMove(0, 501);
        }
        Assert.Equal("game", r.ActivePcId);
    }

    [Fact]
    public void HeldButton_BlocksCrossing()
    {
        // Dragging a scrollbar or window against an edge must not jump to the other PC and abort the drag.
        var r = NewRouter();
        r.OnButton(MouseButtonKind.Left, true);
        r.OnMouseMove(0, 500);
        Assert.False(r.OnMouseMove(-1, 500).Block);
        Assert.Equal("game", r.ActivePcId);
        r.OnButton(MouseButtonKind.Left, false);
        r.OnMouseMove(-1, 500);
        Assert.Equal("stream", r.ActivePcId);
    }

    [Fact]
    public void SessionInterrupted_WhileRemote_ReturnsAndReleases()
    {
        var (r, _) = CrossToA();
        r.OnKey(0x1D, false, true);
        var res = r.OnSessionInterrupted();
        Assert.Equal("game", r.ActivePcId);
        Assert.Equal(new Message[] { new ReleaseAll(), new CursorLeave() }, res.ToRemote);
        Assert.Contains(new UnparkAndShow(0, 500), res.Local);
    }

    [Fact]
    public void SessionInterrupted_OnHost_ForgetsHeldKeys()
    {
        var r = NewRouter();
        r.OnKey(0x1D, false, true);
        r.OnSessionInterrupted();
        r.OnMouseMove(0, 500);
        var cross = r.OnMouseMove(-1, 500);
        Assert.Empty(Assert.Single(cross.Local.OfType<ReleaseLocalKeys>()).Keys);
    }

    [Fact]
    public void Resync_AfterUnlock_AvoidsStaleJump()
    {
        // Game PC with a second monitor placed right of C. While locked (no events) the user moved to that monitor.
        var d2 = new MonitorInfo("game", @"\\.\DISPLAY2", 1920, 1080, 2560, 0, 1.0);
        var layout = new VirtualLayout(
            new[] { VirtualLayoutTests.A, VirtualLayoutTests.B, VirtualLayoutTests.C, d2 },
            new[] { new Placement(VirtualLayoutTests.A.Key, 0, 0), new Placement(VirtualLayoutTests.B.Key, 1920, -180),
                    new Placement(VirtualLayoutTests.C.Key, 4480, 0), new Placement(d2.Key, 6400, 0) });
        var r = new InputRouter(layout, "game", new VPoint(100, 100));
        r.Resync(3000, 500);
        Assert.False(r.OnMouseMove(3001, 500).Block);
        Assert.Equal("game", r.ActivePcId);
    }

    [Fact]
    public void SamePointAwayFromEdge_Passes()
    {
        var r = NewRouter();
        r.OnMouseMove(500, 500);
        Assert.False(r.OnMouseMove(500, 500).Block);
        Assert.Equal("game", r.ActivePcId);
    }

    [Fact]
    public void RemoteMove_SendsCursorMove()
    {
        var (r, _) = CrossToA();
        var res = r.OnMouseMove(1270, 720);
        Assert.True(res.Block);
        Assert.Equal(new CursorMove(1909, 320), Assert.Single(res.ToRemote));
    }

    [Fact]
    public void RemoteMoveBackOntoHost_UnparksAndReleases()
    {
        var (r, _) = CrossToA();
        var res = r.OnMouseMove(1285, 720);
        Assert.True(res.Block);
        Assert.Equal("game", r.ActivePcId);
        Assert.Equal(new Message[] { new ReleaseAll(), new CursorLeave() }, res.ToRemote);
        Assert.Contains(new UnparkAndShow(4, 500), res.Local);
    }

    [Fact]
    public void ReturningOntoHostEdge_DoesNotBounceBack()
    {
        // Coming back from A by 1px lands on B's left edge pixel; the echo of our own SetCursorPos must not push back out.
        var (r, _) = CrossToA();
        var back = r.OnMouseMove(1281, 720);
        Assert.Contains(new UnparkAndShow(0, 500), back.Local);
        Assert.False(r.OnMouseMove(0, 500).Block);
        Assert.Equal("game", r.ActivePcId);
        // A real push after that still crosses.
        r.OnMouseMove(0, 500);
        r.OnMouseMove(0, 500);
        r.OnMouseMove(0, 500);
        Assert.Equal("stream", r.ActivePcId);
    }

    [Fact]
    public void RemoteToOtherRemoteMonitorAcrossHost_GoesThroughHost()
    {
        // From A, heading right never jumps straight to C (the stream PC's own Windows layout has A next to C).
        var (r, _) = CrossToA();
        r.OnMouseMove(1290, 720);
        Assert.Equal("game", r.ActivePcId);
        Assert.Equal("game", VirtualLayoutTests.Layout().MonitorAt(r.Position)!.Info.PcId);
    }

    [Fact]
    public void HostToRightRemote_EntersC()
    {
        var r = NewRouter();
        r.OnMouseMove(2559, 500);
        var res = r.OnMouseMove(2560, 500);
        Assert.Equal("stream", r.ActivePcId);
        Assert.Equal(new CursorEnter(1920, 320), Assert.Single(res.ToRemote)); // C's own OS origin is 1920,0
    }

    [Fact]
    public void CrossingWithHeldKey_ReleasesOnLeavingSide()
    {
        var r = NewRouter();
        Assert.False(r.OnKey(0x1D, false, true).Block);
        r.OnMouseMove(0, 500);
        var cross = r.OnMouseMove(-1, 500);
        var release = Assert.Single(cross.Local.OfType<ReleaseLocalKeys>());
        Assert.Contains((ushort)0x1D, release.Keys.Select(k => k.Scan));
        var up = r.OnKey(0x1D, false, false);
        Assert.True(up.Block);
        Assert.Empty(up.ToRemote);
    }

    [Fact]
    public void RemoteKeysAndButtons_AreForwarded()
    {
        var (r, _) = CrossToA();
        Assert.Equal(new Key(0x1E, false, true), Assert.Single(r.OnKey(0x1E, false, true).ToRemote));
        Assert.Equal(new Key(0x72, false, true, 0x15), Assert.Single(r.OnKey(0x72, false, true, 0x15).ToRemote)); // 한/영 keeps its VK
        Assert.Equal(new Key(0x1E, false, false), Assert.Single(r.OnKey(0x1E, false, false).ToRemote));
        Assert.Equal(new MouseButton(MouseButtonKind.Left, true), Assert.Single(r.OnButton(MouseButtonKind.Left, true).ToRemote));
        Assert.Equal(new MouseWheel(false, 120), Assert.Single(r.OnWheel(false, 120).ToRemote));
    }

    [Fact]
    public void QuickSwitch_ReturnsToLastPosition()
    {
        var (r, _) = CrossToA();
        r.OnMouseMove(1280 - 919, 720 - 20);          // A at virtual (1000,300)
        var back = r.QuickSwitch();
        Assert.Equal("game", r.ActivePcId);
        Assert.Contains(new UnparkAndShow(0, 500), back.Local);
        var again = r.QuickSwitch();
        Assert.Equal("stream", r.ActivePcId);
        Assert.Equal(new CursorEnter(1000, 300), Assert.Single(again.ToRemote));
    }

    [Fact]
    public void QuickSwitch_NoRemote_DoesNothing()
    {
        var r = new InputRouter(new VirtualLayout(new[] { VirtualLayoutTests.B }, Array.Empty<Placement>()), "game", new VPoint(5, 5));
        var res = r.QuickSwitch();
        Assert.Equal("game", r.ActivePcId);
        Assert.Empty(res.ToRemote);
    }

    [Fact]
    public void Disconnect_WhileRemote_ReturnsToHost()
    {
        var (r, _) = CrossToA();
        var res = r.OnDisconnected();
        Assert.Equal("game", r.ActivePcId);
        Assert.Empty(res.ToRemote);
        Assert.Contains(new UnparkAndShow(0, 500), res.Local);
        // With the receiver gone, the left edge no longer crosses.
        r.OnMouseMove(0, 500);
        Assert.False(r.OnMouseMove(-1, 500).Block);
    }

    [Fact]
    public void LayoutChanged_ActiveMonitorRemoved_MovesToHost()
    {
        var (r, _) = CrossToA();
        var withoutA = new VirtualLayout(
            new[] { VirtualLayoutTests.B, VirtualLayoutTests.C },
            new[] { new Placement(VirtualLayoutTests.B.Key, 1920, -180), new Placement(VirtualLayoutTests.C.Key, 4480, 0) });
        var res = r.OnLayoutChanged(withoutA);
        Assert.Equal("game", r.ActivePcId);
        Assert.Equal(new Message[] { new ReleaseAll(), new CursorLeave() }, res.ToRemote);
        Assert.Contains(new UnparkAndShow(0, 500), res.Local);
    }

    [Fact]
    public void RemoteLock_KeepsCursorOnStream()
    {
        var (r, _) = CrossToA();                       // A at virtual (1919,320), park (1280,720)
        Assert.True(r.ToggleRemoteLock());
        var res = r.OnMouseMove(1285, 720);            // push right, toward the game monitor
        Assert.Equal("stream", r.ActivePcId);
        Assert.Equal(new CursorMove(1919, 320), Assert.Single(res.ToRemote));
        Assert.Equal(new CursorMove(1909, 320), Assert.Single(r.OnMouseMove(1270, 720).ToRemote));
    }

    [Fact]
    public void RemoteLock_BlocksQuickSwitch_UntilUnlocked()
    {
        var (r, _) = CrossToA();
        r.ToggleRemoteLock();
        var res = r.QuickSwitch();
        Assert.True(res.Block);
        Assert.Empty(res.ToRemote);
        Assert.Equal("stream", r.ActivePcId);
        Assert.False(r.ToggleRemoteLock());
        r.QuickSwitch();
        Assert.Equal("game", r.ActivePcId);
    }

    [Fact]
    public void RemoteLock_OnHost_DoesNothing()
    {
        var r = NewRouter();
        Assert.False(r.ToggleRemoteLock());
        Assert.False(r.RemoteLocked);
    }

    [Fact]
    public void RemoteLock_ClearedWhenCursorIsSentHome()
    {
        var (r, _) = CrossToA();
        r.ToggleRemoteLock();
        r.OnSessionInterrupted();
        Assert.Equal("game", r.ActivePcId);
        Assert.False(r.RemoteLocked);
    }
}
