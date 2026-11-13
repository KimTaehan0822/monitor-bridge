using System.Threading.Channels;
using MonitorBridge.Core.Protocol;

namespace MonitorBridge.Core.Net;

/// <summary>
/// Message pump over a <see cref="SecureChannel"/>. Input messages always go out before queued bulk (file) messages.
/// Each frame starts with a kind byte: 0 = whole message, 1 = fragment, 2 = last fragment. Only bulk messages fragment,
/// so input can slip in between the pieces of a large clipboard image.
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    readonly SecureChannel _ch;
    readonly TimeSpan _pingInterval, _timeout;
    // Small pieces plus a small socket send buffer keep input from waiting behind bulk data inside TCP.
    public const int FragmentSize = 64 * 1024;
    public const int MaxMessage = 128 * 1024 * 1024;
    const byte Whole = 0, Fragment = 1, Last = 2;

    readonly Channel<byte[]> _high = Channel.CreateUnbounded<byte[]>();
    readonly Channel<byte[]> _bulk = Channel.CreateBounded<byte[]>(16);
    readonly SemaphoreSlim _bulkOrder = new(1, 1);
    MemoryStream? _partial;
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
    public void Send(Message m)
    {
        var body = MessageCodec.Encode(m);
        if (body.Length > FragmentSize) throw new ArgumentException("Large messages must use SendBulkAsync");
        _high.Writer.TryWrite(Frame(Whole, body, 0, body.Length));
    }

    /// <summary>Waits while the bulk queue is full, so a large file never sits in memory all at once.</summary>
    public async ValueTask SendBulkAsync(Message m, CancellationToken ct)
    {
        var body = MessageCodec.Encode(m);
        if (body.Length > MaxMessage) throw new ArgumentException("Message too large");
        await _bulkOrder.WaitAsync(ct);
        try
        {
            if (body.Length <= FragmentSize) { await _bulk.Writer.WriteAsync(Frame(Whole, body, 0, body.Length), ct); return; }
            for (int off = 0; off < body.Length; off += FragmentSize)
            {
                int n = Math.Min(FragmentSize, body.Length - off);
                await _bulk.Writer.WriteAsync(Frame(off + n == body.Length ? Last : Fragment, body, off, n), ct);
            }
        }
        finally { _bulkOrder.Release(); }
    }

    static byte[] Frame(byte kind, byte[] body, int offset, int count)
    {
        var f = new byte[count + 1];
        f[0] = kind;
        Buffer.BlockCopy(body, offset, f, 1, count);
        return f;
    }

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
            if (_high.Reader.TryRead(out var f) || _bulk.Reader.TryRead(out f))
            {
                await _ch.SendAsync(f, ct);
                continue;
            }
            await Task.WhenAny(_high.Reader.WaitToReadAsync(ct).AsTask(), _bulk.Reader.WaitToReadAsync(ct).AsTask());
        }
    }

    async Task ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var f = await _ch.ReceiveAsync(ct);
            Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);
            if (f.Length == 0) throw new InvalidDataException("Empty frame");
            byte[] body;
            switch (f[0])
            {
                case Whole: body = f[1..]; break;
                case Fragment or Last:
                    _partial ??= new MemoryStream();
                    if (_partial.Length + f.Length - 1 > MaxMessage) throw new InvalidDataException("Message too large");
                    _partial.Write(f, 1, f.Length - 1);
                    if (f[0] == Fragment) continue;
                    body = _partial.ToArray();
                    _partial = null;
                    break;
                default: throw new InvalidDataException("Bad frame kind");
            }
            var m = MessageCodec.Decode(body);
            if (m is not Ping and not UnknownMessage) Received?.Invoke(m);
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
