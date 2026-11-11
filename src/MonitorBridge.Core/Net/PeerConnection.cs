using System.Threading.Channels;
using MonitorBridge.Core.Protocol;

namespace MonitorBridge.Core.Net;

/// <summary>
/// Message pump over a <see cref="SecureChannel"/>. Input messages always go out before queued bulk (file) messages.
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    readonly SecureChannel _ch;
    readonly TimeSpan _pingInterval, _timeout;
    readonly Channel<Message> _high = Channel.CreateUnbounded<Message>();
    readonly Channel<Message> _bulk = Channel.CreateBounded<Message>(16);
    long _lastReceivedTicks = Environment.TickCount64;
    int _closed;

    public event Action<Message>? Received;
    public event Action? Closed;

    public PeerConnection(SecureChannel ch, TimeSpan? pingInterval = null, TimeSpan? timeout = null)
    {
        _ch = ch;
        _pingInterval = pingInterval ?? TimeSpan.FromSeconds(2);
        _timeout = timeout ?? TimeSpan.FromSeconds(6);
    }

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    /// <summary>Non-blocking; safe to call from a hook callback.</summary>
    public void Send(Message m) => _high.Writer.TryWrite(m);

    /// <summary>Waits while the bulk queue is full, so a large file never sits in memory all at once.</summary>
    public ValueTask SendBulkAsync(Message m, CancellationToken ct) => _bulk.Writer.WriteAsync(m, ct);

    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var tasks = new[] { WriteLoop(cts.Token), ReadLoop(cts.Token), PingLoop(cts.Token) };
            await Task.WhenAny(tasks);
            cts.Cancel();
            try { await Task.WhenAll(tasks); } catch { /* the first failure already ended the connection */ }
        }
        finally
        {
            await CloseAsync();
        }
    }

    async Task WriteLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_high.Reader.TryRead(out var m) || _bulk.Reader.TryRead(out m))
            {
                await _ch.SendAsync(MessageCodec.Encode(m), ct);
                continue;
            }
            await Task.WhenAny(_high.Reader.WaitToReadAsync(ct).AsTask(), _bulk.Reader.WaitToReadAsync(ct).AsTask());
        }
    }

    async Task ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var m = MessageCodec.Decode(await _ch.ReceiveAsync(ct));
            Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);
            if (m is not Ping) Received?.Invoke(m);
        }
    }

    async Task PingLoop(CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Min(_pingInterval.TotalMilliseconds, _timeout.TotalMilliseconds / 4));
        long lastPing = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(tick, ct);
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastReceivedTicks) > _timeout.TotalMilliseconds) return;
            if (now - lastPing >= _pingInterval.TotalMilliseconds) { Send(new Ping()); lastPing = now; }
        }
    }

    async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _high.Writer.TryComplete();
        _bulk.Writer.TryComplete();
        await _ch.DisposeAsync();
        Closed?.Invoke();
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
