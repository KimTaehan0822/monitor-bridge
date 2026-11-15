using System.Buffers.Binary;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace MonitorBridge.Core.Net;

/// <summary>
/// Password-authenticated, AES-GCM encrypted frames over a stream.
/// Frame: [int32 length][12-byte nonce: direction byte, 3 zero bytes, uint64 counter][ciphertext][16-byte tag].
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    public const int MaxFrame = 1024 * 1024;
    const int NonceSize = 12, TagSize = 16;
    static readonly byte[] Salt = Encoding.UTF8.GetBytes("MonitorBridge-v1");

    readonly Stream _stream;
    // One instance per direction: the sender and the reader run on different threads at the same time.
    readonly AesGcm _sendAes, _recvAes;
    readonly byte _sendDir, _recvDir;
    ulong _sendCounter, _recvCounter;
    readonly SemaphoreSlim _sendLock = new(1, 1);

    SecureChannel(Stream stream, byte[] key, byte sendDir)
    {
        _stream = stream;
        _sendAes = new AesGcm(key, TagSize);
        _recvAes = new AesGcm(key, TagSize);
        _sendDir = sendDir;
        _recvDir = (byte)(1 - sendDir);
    }

    /// <param name="enhanced">Enhanced security mode. Both PCs must match, or both sides throw <see cref="SecurityModeMismatchException"/>.</param>
    public static Task<SecureChannel> ConnectAsync(Stream s, string password, CancellationToken ct, bool enhanced = false) => Handshake(s, password, isClient: true, enhanced, ct);
    public static Task<SecureChannel> AcceptAsync(Stream s, string password, CancellationToken ct, bool enhanced = false) => Handshake(s, password, isClient: false, enhanced, ct);

    // Normal mode keeps the original labels, so it still talks to older builds.
    static byte[] ClientLabel(bool enhanced) => enhanced ? "client-enhanced"u8.ToArray() : "client"u8.ToArray();
    static byte[] ServerLabel(bool enhanced) => enhanced ? "server-enhanced"u8.ToArray() : "server"u8.ToArray();

    static async Task<SecureChannel> Handshake(Stream s, string password, bool isClient, bool enhanced, CancellationToken ct)
    {
        try
        {
            var master = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Salt, 100_000, HashAlgorithmName.SHA256, 32);
            var mine = RandomNumberGenerator.GetBytes(16);
            var theirs = new byte[16];
            await s.WriteAsync(mine, ct);
            await s.ReadExactlyAsync(theirs, ct);
            var (nc, ns) = isClient ? (mine, theirs) : (theirs, mine);
            var session = HMACSHA256.HashData(master, nc.Concat(ns).ToArray());
            byte[] ClientProof(bool e) => HMACSHA256.HashData(session, ClientLabel(e));
            byte[] ServerProof(bool e) => HMACSHA256.HashData(session, ServerLabel(e));
            var proof = new byte[32];
            if (isClient)
            {
                await s.WriteAsync(ClientProof(enhanced), ct);
                await s.ReadExactlyAsync(proof, ct);
                if (CryptographicOperations.FixedTimeEquals(proof, ServerProof(!enhanced))) throw new SecurityModeMismatchException(peerEnhanced: !enhanced);
                if (!CryptographicOperations.FixedTimeEquals(proof, ServerProof(enhanced))) throw new AuthenticationException("비밀번호가 맞지 않아요");
            }
            else
            {
                await s.ReadExactlyAsync(proof, ct);
                bool mismatch = CryptographicOperations.FixedTimeEquals(proof, ClientProof(!enhanced));
                if (!mismatch && !CryptographicOperations.FixedTimeEquals(proof, ClientProof(enhanced))) throw new AuthenticationException("비밀번호가 맞지 않아요");
                // The password is right either way: answer so the game PC learns the modes differ, then refuse.
                await s.WriteAsync(ServerProof(enhanced), ct);
                if (mismatch) throw new SecurityModeMismatchException(peerEnhanced: !enhanced);
            }
            return new SecureChannel(s, HMACSHA256.HashData(session, "enc"u8), isClient ? (byte)0 : (byte)1);
        }
        catch (Exception e) when (e is EndOfStreamException or IOException)
        {
            // The peer drops the connection when it rejects our password.
            s.Dispose();
            throw new AuthenticationException("인증 중에 연결이 끊겼어요(비밀번호가 다를 수 있어요)", e);
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        if (plaintext.Length > MaxFrame - NonceSize - TagSize) throw new ArgumentException("Frame too large");
        var frame = new byte[4 + NonceSize + plaintext.Length + TagSize];
        BinaryPrimitives.WriteInt32LittleEndian(frame, frame.Length - 4);
        await _sendLock.WaitAsync(ct);
        try
        {
            Encrypt(frame, plaintext.Span);
            await _stream.WriteAsync(frame, ct);
        }
        finally { _sendLock.Release(); }
    }

    void Encrypt(byte[] frame, ReadOnlySpan<byte> plaintext)
    {
        var nonce = frame.AsSpan(4, NonceSize);
        nonce[0] = _sendDir;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], ++_sendCounter);
        _sendAes.Encrypt(nonce, plaintext, frame.AsSpan(4 + NonceSize, plaintext.Length), frame.AsSpan(frame.Length - TagSize));
    }

    /// <summary>Only one reader at a time.</summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await _stream.ReadExactlyAsync(header, ct);
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len < NonceSize + TagSize || len > MaxFrame) throw new InvalidDataException($"Bad frame length {len}");
        var body = new byte[len];
        await _stream.ReadExactlyAsync(body, ct);
        return Decrypt(body);
    }

    byte[] Decrypt(byte[] body)
    {
        var nonce = body.AsSpan(0, NonceSize);
        ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(nonce[4..]);
        if (nonce[0] != _recvDir || nonce[1] != 0 || nonce[2] != 0 || nonce[3] != 0) throw new InvalidDataException("Bad frame direction");
        if (counter <= _recvCounter) throw new InvalidDataException("Replayed frame");
        var plain = new byte[body.Length - NonceSize - TagSize];
        _recvAes.Decrypt(nonce, body.AsSpan(NonceSize, plain.Length), body.AsSpan(body.Length - TagSize), plain);
        _recvCounter = counter;
        return plain;
    }

    public ValueTask DisposeAsync()
    {
        _sendAes.Dispose();
        _recvAes.Dispose();
        return _stream.DisposeAsync();
    }
}

/// <summary>The password is right, but only one of the two PCs has enhanced security mode on.</summary>
public sealed class SecurityModeMismatchException(bool peerEnhanced)
    : AuthenticationException(peerEnhanced ? "상대 PC만 향상된 보안 모드를 켰어요" : "이 PC만 향상된 보안 모드를 켰어요")
{
    public bool PeerEnhanced { get; } = peerEnhanced;
}
