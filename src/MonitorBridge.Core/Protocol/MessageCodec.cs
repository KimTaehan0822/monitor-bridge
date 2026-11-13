using System.Text;
using MonitorBridge.Core.Layout;

namespace MonitorBridge.Core.Protocol;

/// <summary>Binary wire format: one type byte, then the fields in declaration order.</summary>
public static class MessageCodec
{
    public static byte[] Encode(Message m)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        switch (m)
        {
            case MonitorList x:
                w.Write((byte)1); w.Write(x.Monitors.Length);
                foreach (var i in x.Monitors)
                {
                    w.Write(i.PcId); w.Write(i.DeviceName); w.Write(i.Width); w.Write(i.Height);
                    w.Write(i.OsX); w.Write(i.OsY); w.Write(i.Scale);
                }
                break;
            case CursorEnter x: w.Write((byte)2); w.Write(x.OsX); w.Write(x.OsY); break;
            case CursorMove x: w.Write((byte)3); w.Write(x.OsX); w.Write(x.OsY); break;
            case CursorLeave: w.Write((byte)4); break;
            case MouseButton x: w.Write((byte)5); w.Write((byte)x.Button); w.Write(x.Down); break;
            case MouseWheel x: w.Write((byte)6); w.Write(x.Horizontal); w.Write(x.Delta); break;
            case Key x: w.Write((byte)7); w.Write(x.ScanCode); w.Write(x.Extended); w.Write(x.Down); w.Write(x.Vk); break;
            case ReleaseAll: w.Write((byte)8); break;
            case ClipboardText x: w.Write((byte)9); w.Write(x.Text); break;
            case ClipboardImage x: w.Write((byte)10); WriteBytes(w, x.Png); break;
            case FileOfferStart x: w.Write((byte)11); w.Write(x.Id.ToByteArray()); w.Write(x.TotalBytes); w.Write(x.Count); break;
            case FileEntry x: w.Write((byte)12); w.Write(x.Id.ToByteArray()); w.Write(x.RelativePath); w.Write(x.IsDirectory); w.Write(x.Length); break;
            case FileChunk x: w.Write((byte)13); w.Write(x.Id.ToByteArray()); WriteBytes(w, x.Data); break;
            case FileOfferEnd x: w.Write((byte)14); w.Write(x.Id.ToByteArray()); break;
            case FileCancel x: w.Write((byte)15); w.Write(x.Id.ToByteArray()); break;
            case Ping: w.Write((byte)16); break;
            case Highlight: w.Write((byte)17); break;
            default: throw new ArgumentException($"Unknown message {m.GetType().Name}");
        }
        w.Flush();
        return ms.ToArray();
    }

    public static Message Decode(ReadOnlySpan<byte> data)
    {
        using var r = new BinaryReader(new MemoryStream(data.ToArray()), Encoding.UTF8);
        try
        {
            Message m = r.ReadByte() switch
            {
                1 => new MonitorList(ReadMonitors(r)),
                2 => new CursorEnter(r.ReadInt32(), r.ReadInt32()),
                3 => new CursorMove(r.ReadInt32(), r.ReadInt32()),
                4 => new CursorLeave(),
                5 => new MouseButton(ReadEnum(r), r.ReadBoolean()),
                6 => new MouseWheel(r.ReadBoolean(), r.ReadInt32()),
                7 => new Key(r.ReadUInt16(), r.ReadBoolean(), r.ReadBoolean(), r.ReadUInt16()),
                8 => new ReleaseAll(),
                9 => new ClipboardText(r.ReadString()),
                10 => new ClipboardImage(ReadBytes(r)),
                11 => new FileOfferStart(ReadGuid(r), r.ReadInt64(), r.ReadInt32()),
                12 => new FileEntry(ReadGuid(r), r.ReadString(), r.ReadBoolean(), r.ReadInt64()),
                13 => new FileChunk(ReadGuid(r), ReadBytes(r)),
                14 => new FileOfferEnd(ReadGuid(r)),
                15 => new FileCancel(ReadGuid(r)),
                16 => new Ping(),
                17 => new Highlight(),
                var t => throw new InvalidDataException($"Unknown message type {t}"),
            };
            return m;
        }
        catch (EndOfStreamException e) { throw new InvalidDataException("Truncated message", e); }
    }

    static void WriteBytes(BinaryWriter w, byte[] b) { w.Write(b.Length); w.Write(b); }

    static byte[] ReadBytes(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Bad length");
        return r.ReadBytes(n);
    }

    static Guid ReadGuid(BinaryReader r)
    {
        var b = r.ReadBytes(16);
        if (b.Length != 16) throw new EndOfStreamException();
        return new Guid(b);
    }

    static MouseButtonKind ReadEnum(BinaryReader r)
    {
        var b = r.ReadByte();
        if (b > (byte)MouseButtonKind.X2) throw new InvalidDataException("Bad button");
        return (MouseButtonKind)b;
    }

    static MonitorInfo[] ReadMonitors(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > 64) throw new InvalidDataException("Bad monitor count");
        var list = new MonitorInfo[n];
        for (int i = 0; i < n; i++)
            list[i] = new MonitorInfo(r.ReadString(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadDouble());
        return list;
    }
}
