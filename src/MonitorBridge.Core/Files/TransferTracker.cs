namespace MonitorBridge.Core.Files;

/// <summary>
/// Progress of the latest send and the latest receive, kept apart so one never overwrites the other.
/// Not thread-safe: the caller holds its own lock.
/// </summary>
public sealed class TransferTracker
{
    sealed class Slot
    {
        public Guid Id;
        public string Name = "";
        public long Total, Done, Start, End;
        public TransferState State;
        public bool Active;
    }

    readonly Slot _send = new(), _recv = new();
    readonly string _peerName;
    readonly Func<long> _now;

    public TransferTracker(string peerName, Func<long>? now = null)
    {
        _peerName = peerName;
        _now = now ?? (() => Environment.TickCount64);
    }

    Slot S(bool sending) => sending ? _send : _recv;

    /// <summary>Starts tracking; an older transfer in the same direction is replaced.</summary>
    public void Begin(bool sending, Guid id, string name, long total)
    {
        var s = S(sending);
        s.Id = id;
        s.Name = name;
        s.Total = total;
        s.Done = 0;
        s.Start = _now();
        s.End = 0;
        s.State = TransferState.Running;
        s.Active = true;
    }

    public void SetName(bool sending, Guid id, string name)
    {
        var s = S(sending);
        if (s.Active && s.Id == id) s.Name = name;
    }

    public void Advance(bool sending, Guid id, long bytes)
    {
        var s = S(sending);
        if (s.Active && s.Id == id && s.State == TransferState.Running) s.Done += bytes;
    }

    /// <summary>True only for the first end of a transfer still tracked, so exactly one path tells the person.</summary>
    public bool Finish(bool sending, Guid id, TransferState state)
    {
        var s = S(sending);
        if (!s.Active || s.Id != id || s.State != TransferState.Running) return false;
        s.State = state;
        s.End = _now();
        return true;
    }

    /// <summary>The link dropped: every running transfer has failed. True if there was one.</summary>
    public bool FailAll()
    {
        bool any = false;
        foreach (var s in new[] { _send, _recv })
            if (s.Active && s.State == TransferState.Running) { s.State = TransferState.Failed; s.End = _now(); any = true; }
        return any;
    }

    /// <summary>The newest running transfer; if none runs, the one that ended last.</summary>
    public TransferProgress? Current
    {
        get
        {
            var running = new[] { _send, _recv }.Where(s => s.Active && s.State == TransferState.Running).MaxBy(s => s.Start);
            var s = running ?? new[] { _send, _recv }.Where(s => s.Active).MaxBy(s => s.End);
            if (s is null) return null;
            long end = s.State == TransferState.Running ? _now() : s.End;
            return new TransferProgress(s == _send, _peerName, s.Name, s.Total, s.Done, end - s.Start, s.State);
        }
    }
}
