using System.Net;

namespace MonitorBridge.Core.Net;

/// <summary>Blocks an address for a while after too many wrong passwords in a row. Thread-safe.</summary>
public sealed class AuthLimiter(int maxFailures, TimeSpan lockout, Func<long>? clockMs = null)
{
    readonly Func<long> _now = clockMs ?? (() => Environment.TickCount64);
    // shortcut: entries are never pruned; fine for a home LAN's handful of addresses, prune if this ever faces the internet.
    readonly Dictionary<IPAddress, (int Failures, long BlockedUntil)> _state = new();

    public bool IsBlocked(IPAddress ip)
    {
        lock (_state) return _state.TryGetValue(ip, out var s) && _now() < s.BlockedUntil;
    }

    /// <summary>Records a wrong password. True when this failure starts a block.</summary>
    public bool Fail(IPAddress ip)
    {
        lock (_state)
        {
            _state.TryGetValue(ip, out var s);
            if (s.BlockedUntil != 0 && _now() >= s.BlockedUntil) s = default; // the block ran out: start counting again
            s.Failures++;
            if (s.Failures < maxFailures) { _state[ip] = s; return false; }
            _state[ip] = (0, _now() + (long)lockout.TotalMilliseconds);
            return true;
        }
    }

    public void Succeed(IPAddress ip)
    {
        lock (_state) _state.Remove(ip);
    }
}
