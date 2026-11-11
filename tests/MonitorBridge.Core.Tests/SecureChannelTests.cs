using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using MonitorBridge.Core.Net;

namespace MonitorBridge.Core.Tests;

public class SecureChannelTests
{
    internal static async Task<(NetworkStream Client, NetworkStream Server)> TcpPair()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var c = new TcpClient();
        var connect = c.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
        var s = await l.AcceptTcpClientAsync();
        await connect;
        l.Stop();
        return (c.GetStream(), s.GetStream());
    }

    internal static async Task<(SecureChannel Client, SecureChannel Server, NetworkStream RawClient)> Pair(string pc = "pw", string ps = "pw")
    {
        var (c, s) = await TcpPair();
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
        var ca = SecureChannel.ConnectAsync(c, pc, ct);
        var sa = SecureChannel.AcceptAsync(s, ps, ct);
        return (await ca, await sa, c);
    }

    [Fact]
    public async Task SamePassword_RoundTripsData()
    {
        var (c, s, _) = await Pair();
        await c.SendAsync(new byte[] { 1, 2, 3 }, default);
        Assert.Equal(new byte[] { 1, 2, 3 }, await s.ReceiveAsync(default));
        await s.SendAsync(new byte[] { 9 }, default);
        Assert.Equal(new byte[] { 9 }, await c.ReceiveAsync(default));
    }

    [Fact]
    public async Task WrongPassword_BothSidesThrowAuthenticationException()
    {
        var (c, s) = await TcpPair();
        var ca = SecureChannel.ConnectAsync(c, "right", default);
        var sa = SecureChannel.AcceptAsync(s, "wrong", default);
        await Assert.ThrowsAsync<AuthenticationException>(() => sa);
        await Assert.ThrowsAsync<AuthenticationException>(() => ca);
    }

    [Fact]
    public async Task TamperedFrame_Throws()
    {
        var (_, s, raw) = await Pair();
        // A well-formed frame from the client direction with a forged ciphertext/tag.
        var frame = new byte[4 + 12 + 5 + 16];
        BinaryPrimitives.WriteInt32LittleEndian(frame, frame.Length - 4);
        frame[4] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(8), 99);
        await raw.WriteAsync(frame);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => s.ReceiveAsync(default));
    }

    [Fact]
    public async Task OversizedFrame_Throws()
    {
        var (_, s, raw) = await Pair();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 2 * 1024 * 1024);
        await raw.WriteAsync(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => s.ReceiveAsync(default));
    }

    [Fact]
    public async Task ReplayedCounter_Throws()
    {
        var (c, s) = await TcpPair();
        var tap = new TapStream(c);
        var ca = SecureChannel.ConnectAsync(tap, "pw", default);
        var server = await SecureChannel.AcceptAsync(s, "pw", default);
        var client = await ca;
        tap.Captured.SetLength(0);
        await client.SendAsync(new byte[] { 7 }, default);
        Assert.Equal(new byte[] { 7 }, await server.ReceiveAsync(default));
        await c.WriteAsync(tap.Captured.ToArray()); // same frame again
        await Assert.ThrowsAsync<InvalidDataException>(() => server.ReceiveAsync(default));
    }

    /// <summary>Records everything written through it.</summary>
    sealed class TapStream(Stream inner) : Stream
    {
        public MemoryStream Captured { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken ct = default) => inner.ReadAsync(m, ct);
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) { Captured.Write(b, o, c); inner.Write(b, o, c); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> m, CancellationToken ct = default) { Captured.Write(m.Span); return inner.WriteAsync(m, ct); }
    }
}
