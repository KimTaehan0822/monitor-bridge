namespace MonitorBridge.Core.Layout;

/// <summary>A physical monitor as its own PC sees it. Sizes and OS coordinates are physical pixels.</summary>
public sealed record MonitorInfo(string PcId, string DeviceName, int Width, int Height, int OsX, int OsY, double Scale)
{
    public string Key => $"{PcId}/{DeviceName}";
}

/// <summary>Where the user put a monitor on the shared virtual desktop.</summary>
public sealed record Placement(string Key, int X, int Y);

public readonly record struct VPoint(int X, int Y);

public sealed record PlacedMonitor(MonitorInfo Info, int X, int Y)
{
    public int Right => X + Info.Width;
    public int Bottom => Y + Info.Height;
    public bool Contains(VPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
    public VPoint Center => new(X + Info.Width / 2, Y + Info.Height / 2);
}
