using MonitorBridge.Core.Layout;

namespace MonitorBridge.Core.Protocol;

public enum MouseButtonKind : byte { Left, Right, Middle, X1, X2 }

public abstract record Message;
public sealed record MonitorList(MonitorInfo[] Monitors) : Message;
public sealed record CursorEnter(int OsX, int OsY) : Message;
public sealed record CursorMove(int OsX, int OsY) : Message;
public sealed record CursorLeave() : Message;
public sealed record MouseButton(MouseButtonKind Button, bool Down) : Message;
public sealed record MouseWheel(bool Horizontal, int Delta) : Message;
/// <summary>Vk is kept for keys a scan code cannot express (한/영, 한자, Pause); 0 = use the scan code.</summary>
public sealed record Key(ushort ScanCode, bool Extended, bool Down, ushort Vk = 0) : Message;
public sealed record ReleaseAll() : Message;
public sealed record ClipboardText(string Text) : Message;
public sealed record ClipboardImage(byte[] Png) : Message;
public sealed record FileOfferStart(Guid Id, long TotalBytes, int Count) : Message;
public sealed record FileEntry(Guid Id, string RelativePath, bool IsDirectory, long Length) : Message;
public sealed record FileChunk(Guid Id, byte[] Data) : Message;
public sealed record FileOfferEnd(Guid Id) : Message;
public sealed record FileCancel(Guid Id) : Message;
public sealed record Ping() : Message;
/// <summary>Show a ripple where the cursor is now, so it is easy to find after a quick switch.</summary>
public sealed record Highlight() : Message;
/// <summary>This PC's settings the peer needs to know: its file copy limit (MB).</summary>
public sealed record PeerSettings(int FileLimitMB) : Message;
/// <summary>"I copied these files" — names and size only. Count 0 withdraws the earlier offer.</summary>
public sealed record FileListOffer(Guid Id, int Count, long TotalBytes, string FirstName) : Message;
/// <summary>"Send me the files you offered" (right-click paste on the other PC).</summary>
public sealed record FileRequest(Guid Id) : Message;
/// <summary>Sent on connect: which build this PC runs, so a mismatch can be shown instead of breaking the link.</summary>
public sealed record Hello(string Build) : Message;
/// <summary>This PC's file send speed limit (MB/s, 0 = none). Its own type so older builds simply ignore it.</summary>
public sealed record PeerSpeed(int MBps) : Message;
/// <summary>A message type from a newer build. Decoded as this and ignored, so the link stays up.</summary>
public sealed record UnknownMessage(byte Type) : Message;
