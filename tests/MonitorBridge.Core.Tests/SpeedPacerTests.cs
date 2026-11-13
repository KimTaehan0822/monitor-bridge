using MonitorBridge.Core.Files;

namespace MonitorBridge.Core.Tests;

public class SpeedPacerTests
{
    const int MiB = 1 << 20;
    long _now;
    SpeedPacer New() => new(() => _now);

    [Fact]
    public void Unlimited_NeverWaits() => Assert.Equal(0, New().DelayMs(MiB, 0));

    [Fact]
    public void TenMBps_OneMiB_Waits100ms() => Assert.InRange(New().DelayMs(MiB, 10), 99, 101);

    [Fact]
    public void Steady_KeepsPace()
    {
        var p = New();
        p.DelayMs(MiB, 10);
        _now += 100;
        Assert.InRange(p.DelayMs(MiB, 10), 99, 101);
    }

    [Fact]
    public void SlowSender_DoesNotBurst()
    {
        var p = New();
        p.DelayMs(MiB, 10);
        _now += 5000; // the disk was slow; that time is not saved up for a burst later
        Assert.InRange(p.DelayMs(MiB, 10), 99, 101);
    }

    [Fact]
    public void RateChange_StartsFresh()
    {
        var p = New();
        for (int i = 0; i < 3; i++) p.DelayMs(MiB, 10);
        Assert.InRange(p.DelayMs(MiB, 30), 32, 35);
    }
}
