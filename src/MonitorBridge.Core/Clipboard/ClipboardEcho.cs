using System.Security.Cryptography;

namespace MonitorBridge.Core.Clipboard;

/// <summary>Stops a clipboard item we just received from being sent straight back.</summary>
public sealed class ClipboardEcho
{
    byte[]? _last;
    readonly object _gate = new();

    public void MarkReceived(byte[] content)
    {
        lock (_gate) _last = SHA256.HashData(content);
    }

    public bool IsEcho(byte[] content)
    {
        lock (_gate) return _last is not null && SHA256.HashData(content).AsSpan().SequenceEqual(_last);
    }
}
