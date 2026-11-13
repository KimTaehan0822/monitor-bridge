using Microsoft.Win32;
using MonitorBridge.Shared;

namespace MonitorBridge.Core.Tests;

public class ShellMenuTests : IDisposable
{
    readonly string _sub = @"Software\MonitorBridgeTest\" + Guid.NewGuid().ToString("N");
    readonly string _dir = Path.Combine(Path.GetTempPath(), "mb-paste-" + Guid.NewGuid().ToString("N"));

    public ShellMenuTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_sub, false);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Register_WritesThreeVerbs()
    {
        using (var root = Registry.CurrentUser.CreateSubKey(_sub)) ShellMenu.Register(root, @"C:\MB\MonitorBridge.exe");
        foreach (var place in new[] { @"Directory\Background", "Directory", "DesktopBackground" })
        {
            using var verb = Registry.CurrentUser.OpenSubKey($@"{_sub}\{place}\shell\MonitorBridge.Paste")!;
            Assert.Equal("Monitor Bridge: 상대 PC에서 복사한 파일 여기에 붙여넣기", verb.GetValue(""));
            Assert.Equal("\"C:\\MB\\MonitorBridge.exe\",0", verb.GetValue("Icon"));
            using var cmd = verb.OpenSubKey("command")!;
            Assert.Equal("\"C:\\MB\\MonitorBridge.exe\" --paste \"%V\"", cmd.GetValue(""));
        }
        using (var root = Registry.CurrentUser.OpenSubKey(_sub, true)!) ShellMenu.Unregister(root);
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{_sub}\Directory\shell\MonitorBridge.Paste"));
    }

    [Fact]
    public void PasteArgs_DriveRoot() => Assert.Equal(@"D:\", PasteArgs.Folder(new[] { "--paste", "D:\"" }));

    [Fact]
    public void PasteArgs_Normal() => Assert.Equal(@"C:\a b", PasteArgs.Folder(new[] { "--paste", @"C:\a b" }));

    [Fact]
    public void PasteArgs_Missing() => Assert.Null(PasteArgs.Folder(new[] { "--selftest" }));

    [Fact]
    public void PasteChannel_RoundTrip()
    {
        string? got = null;
        using var gotIt = new ManualResetEventSlim();
        using (PasteChannel.Start(_dir, f => { got = f; gotIt.Set(); }))
        {
            Assert.True(PasteChannel.TrySend(_dir, @"C:\x", 3000));
            Assert.True(gotIt.Wait(3000));
        }
        Assert.Equal(@"C:\x", got);
    }

    [Fact]
    public void PasteChannel_NobodyListening_ReturnsFalse() =>
        Assert.False(PasteChannel.TrySend(_dir, @"C:\x", 300));
}
