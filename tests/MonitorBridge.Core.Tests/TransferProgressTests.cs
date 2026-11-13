using MonitorBridge.Core.Files;

namespace MonitorBridge.Core.Tests;

public class TransferProgressTests
{
    static TransferProgress P(bool sending, long done, long elapsed, TransferState s = TransferState.Running) =>
        new(sending, "송출컴", "a.mkv", 10L << 30, done, elapsed, s);

    [Fact]
    public void Describe_Running() =>
        Assert.Equal("파일 받는 중 25% · 2.5/10.0GB · 102MB/s · 약 2분 남음", P(false, 5L << 29, 25_000).Describe());

    [Fact]
    public void Describe_Sending_Done() =>
        Assert.Equal("다 보냈어요: a.mkv", P(true, 10L << 30, 100_000, TransferState.Done).Describe());

    [Fact]
    public void Describe_Received_Done() =>
        Assert.Equal("다 받았어요: a.mkv", P(false, 10L << 30, 100_000, TransferState.Done).Describe());

    [Fact]
    public void Describe_Failed() =>
        Assert.Equal("전송 실패: a.mkv", P(false, 1L << 30, 10_000, TransferState.Failed).Describe());

    [Fact]
    public void Describe_Cancelled() =>
        Assert.Equal("전송 취소됨: a.mkv", P(true, 1L << 30, 10_000, TransferState.Cancelled).Describe());

    [Fact]
    public void Describe_NoSpeedYet() =>
        Assert.Equal("파일 보내는 중 0% · 0.0/10.0GB · 계산 중", P(true, 0, 0).Describe());

    [Fact]
    public void Describe_SmallTotal_UsesMB() =>
        Assert.Equal("파일 받는 중 50% · 210/420MB · 21MB/s · 약 1분 남음",
            new TransferProgress(false, "게임컴", "b.zip", 420L << 20, 210L << 20, 10_000, TransferState.Running).Describe());

    [Fact]
    public void Size_Formats()
    {
        Assert.Equal("420MB", TransferProgress.Size(420L << 20));
        Assert.Equal("69.6GB", TransferProgress.Size((long)(69.6 * (1L << 30))));
    }

    [Fact]
    public void EffectiveLimit_UnknownPeer_UsesOwn() => Assert.Equal(500, TransferProgress.EffectiveLimit(500, null));

    [Fact]
    public void EffectiveLimit_TakesSmaller() => Assert.Equal(500, TransferProgress.EffectiveLimit(80000, 500));

    [Fact]
    public void Tooltip_IsCappedAt127()
    {
        Assert.Equal("상태\n진행", TransferProgress.Tooltip("상태", "진행"));
        Assert.Equal("상태", TransferProgress.Tooltip("상태", null));
        Assert.True(TransferProgress.Tooltip(new string('가', 100), new string('나', 100)).Length <= 127);
    }
}
