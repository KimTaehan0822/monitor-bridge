using MonitorBridge.Core.Layout;
using MonitorBridge.Core.Protocol;

namespace MonitorBridge.Core.Tests;

public class MessageCodecTests
{
    static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static TheoryData<Message> All() => new()
    {
        new MonitorList(new[] { VirtualLayoutTests.A, VirtualLayoutTests.B }),
        new CursorEnter(-5, 1079),
        new CursorMove(1920, 7),
        new CursorLeave(),
        new MouseButton(MouseButtonKind.X2, true),
        new MouseWheel(true, -120),
        new Key(0x1D, true, false),
        new Key(0x72, false, true, 0x15),
        new ReleaseAll(),
        new Highlight(),
        new PeerSettings(500),
        new PeerSpeed(30),
        new PeerSpeed(0),
        new FileListOffer(Guid.Parse("11111111-2222-3333-4444-555555555555"), 3, 74_732_929_434, "녹화.mkv"),
        new FileRequest(Guid.Parse("11111111-2222-3333-4444-555555555555")),
        new Hello("0.1.0+202611140030"),
        new ClipboardText("한글 text 😀"),
        new ClipboardImage(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        new FileOfferStart(Id, 123_456_789_012, 3),
        new FileEntry(Id, @"a\b\c.txt", false, 42),
        new FileChunk(Id, new byte[] { 1, 2, 3 }),
        new FileOfferEnd(Id),
        new FileCancel(Id),
        new Ping(),
    };

    [Theory]
    [MemberData(nameof(All))]
    public void RoundTrip(Message m)
    {
        var back = MessageCodec.Decode(MessageCodec.Encode(m));
        Assert.Equal(m.GetType(), back.GetType());
        switch (m)
        {
            case MonitorList l: Assert.Equal(l.Monitors, ((MonitorList)back).Monitors); break;
            case ClipboardImage i: Assert.Equal(i.Png, ((ClipboardImage)back).Png); break;
            case FileChunk c: Assert.Equal(c.Data, ((FileChunk)back).Data); break;
            default: Assert.Equal(m, back); break;
        }
    }

    [Fact]
    public void Decode_Truncated_Throws()
    {
        var full = MessageCodec.Encode(new CursorMove(1, 2));
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(full.AsSpan(0, full.Length - 1)));
    }

    [Fact]
    public void Decode_Empty_Throws() =>
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void UnknownType_FromANewerBuild_IsNotAnError() =>
        Assert.Equal(new UnknownMessage(200), MessageCodec.Decode(new byte[] { 200, 9, 9, 9 }));
}
