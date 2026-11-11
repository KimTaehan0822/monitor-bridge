using System.Collections.Concurrent;
using MonitorBridge.Core.Net;
using MonitorBridge.Core.Protocol;

namespace MonitorBridge.Core.Tests;

public class PeerConnectionTests
{
    [Fact]
    public async Task InputBeatsBulk()
    {
        var (c, s, _) = await SecureChannelTests.Pair();
        var sender = new PeerConnection(c);
        var receiver = new PeerConnection(s);
        var got = new BlockingCollection<Message>();
        receiver.Received += got.Add;

        var id = Guid.NewGuid();
        for (int i = 0; i < 10; i++) await sender.SendBulkAsync(new FileChunk(id, new byte[64 * 1024]), default);
        sender.Send(new Key(0x1E, false, true));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = sender.RunAsync(cts.Token);
        _ = receiver.RunAsync(cts.Token);

        int index = 0;
        while (got.TryTake(out var m, TimeSpan.FromSeconds(5)))
        {
            if (m is Key) break;
            index++;
        }
        Assert.True(index <= 3, $"Key arrived after {index} bulk messages");
        cts.Cancel();
    }

    [Fact]
    public async Task NoTraffic_ClosesAfterTimeout()
    {
        var (c, _, _) = await SecureChannelTests.Pair();
        var conn = new PeerConnection(c, pingInterval: TimeSpan.FromSeconds(30), timeout: TimeSpan.FromMilliseconds(300));
        bool closed = false;
        conn.Closed += () => closed = true;
        var run = conn.RunAsync(default);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3))));
        Assert.True(closed);
    }

    [Fact]
    public async Task PingsKeepConnectionAlive()
    {
        var (c, s, _) = await SecureChannelTests.Pair();
        var a = new PeerConnection(c, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(400));
        var b = new PeerConnection(s, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(400));
        using var cts = new CancellationTokenSource();
        var ra = a.RunAsync(cts.Token);
        var rb = b.RunAsync(cts.Token);
        await Task.Delay(1000);
        Assert.False(ra.IsCompleted);
        Assert.False(rb.IsCompleted);
        cts.Cancel();
    }
}
