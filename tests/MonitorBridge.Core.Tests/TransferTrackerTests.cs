using MonitorBridge.Core.Files;

namespace MonitorBridge.Core.Tests;

public class TransferTrackerTests
{
    long _now = 1000;
    TransferTracker New() => new("송출컴", () => _now);
    static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    [Fact]
    public void SendWhileReceiving_KeepsReceiveRunning()
    {
        var t = New();
        t.Begin(false, A, "big.mkv", 1000);
        t.Advance(false, A, 400);
        _now += 10;
        t.Begin(true, B, "small.txt", 10);              // copying a file while receiving starts a send
        Assert.Equal("small.txt", t.Current!.Name);      // the newest running transfer is shown
        Assert.True(t.Finish(true, B, TransferState.Done));
        Assert.Equal(TransferState.Running, t.Current!.State);
        Assert.Equal("big.mkv", t.Current.Name);
        t.Advance(false, A, 600);
        Assert.Equal(1000, t.Current.Done);
        Assert.True(t.Finish(false, A, TransferState.Done));
    }

    [Fact]
    public void Finish_ReportsOnlyOnce()
    {
        var t = New();
        t.Begin(true, A, "a", 10);
        Assert.True(t.Finish(true, A, TransferState.Failed));
        Assert.False(t.FailAll());                      // the disconnect path must not notify twice
        Assert.False(t.Finish(true, A, TransferState.Done));
    }

    [Fact]
    public void FailAll_FailsEveryRunningTransfer()
    {
        var t = New();
        t.Begin(true, A, "a", 10);
        t.Begin(false, B, "b", 10);
        Assert.True(t.FailAll());
        Assert.Equal(TransferState.Failed, t.Current!.State);
        Assert.False(t.Finish(false, B, TransferState.Done));
    }

    [Fact]
    public void NewTransferSameDirection_ReplacesOld()
    {
        var t = New();
        t.Begin(true, A, "a", 10);
        t.Begin(true, B, "b", 10);
        Assert.False(t.Finish(true, A, TransferState.Done)); // a late finish of the replaced one changes nothing
        Assert.Equal("b", t.Current!.Name);
        Assert.Equal(TransferState.Running, t.Current.State);
    }

    [Fact]
    public void Current_FrozenAfterFinish()
    {
        var t = New();
        t.Begin(false, A, "a", 100);
        _now += 5000;
        t.Finish(false, A, TransferState.Done);
        _now += 60_000;
        Assert.Equal(5000, t.Current!.ElapsedMs);
        Assert.Equal("송출컴", t.Current.PeerName);
    }
}
