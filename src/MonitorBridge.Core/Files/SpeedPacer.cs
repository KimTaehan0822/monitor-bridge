namespace MonitorBridge.Core.Files;

/// <summary>
/// Keeps a send at or under a MB/s limit: after each piece, says how long to wait. Time lost to a slow disk is
/// not saved up, so the link never gets a catch-up burst; a changed limit starts counting afresh.
/// </summary>
public sealed class SpeedPacer
{
    const long MiB = 1L << 20;
    const long MaxLagMs = 1000;
    readonly Func<long> _nowMs;
    long _startMs;
    long _sent;
    int _mbps = -1;

    public SpeedPacer(Func<long> nowMs) => _nowMs = nowMs;

    /// <summary>Call right after sending <paramref name="bytes"/>; returns the milliseconds to wait (0 = go on).</summary>
    public int DelayMs(int bytes, int mbps)
    {
        long now = _nowMs();
        if (mbps <= 0) { _mbps = mbps; return 0; }
        if (mbps != _mbps) { _mbps = mbps; _startMs = now; _sent = 0; }
        _sent += bytes;
        long wait = _startMs + _sent * 1000 / (mbps * MiB) - now;
        if (wait < -MaxLagMs)
        {
            _startMs = now;
            _sent = bytes;
            wait = _sent * 1000 / (mbps * MiB);
        }
        return (int)Math.Max(0, wait);
    }
}
