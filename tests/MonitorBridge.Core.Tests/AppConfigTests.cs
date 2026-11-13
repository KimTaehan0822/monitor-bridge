using MonitorBridge.Core.Config;
using MonitorBridge.Core.Layout;

namespace MonitorBridge.Core.Tests;

public class AppConfigTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mb-cfg").FullName;
    string PathOf(string n) => System.IO.Path.Combine(_dir, n);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var c = AppConfig.Load(PathOf("none.json"));
        Assert.Equal(24850, c.Port);
        Assert.Equal("Scroll", c.LockHotkey);
        Assert.Equal("Ctrl+Alt+Oem3", c.QuickSwitchHotkey);
        Assert.Equal(500, c.FileSizeLimitMB);
        Assert.Equal(30, c.FileSpeedLimitMBps);
        Assert.Equal(Role.Host, c.Role);
    }

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var p = PathOf("sub/config.json");
        var c = new AppConfig { Role = Role.Receiver, Password = "pw", ReceiverAddress = "192.168.0.5", AutoStart = true, FileSpeedLimitMBps = 0 };
        c.Placements.Add(new Placement("game/X", 10, -20));
        c.Save(p);
        var back = AppConfig.Load(p);
        Assert.Equal(Role.Receiver, back.Role);
        Assert.Equal("pw", back.Password);
        Assert.Equal("192.168.0.5", back.ReceiverAddress);
        Assert.True(back.AutoStart);
        Assert.Equal(0, back.FileSpeedLimitMBps); // 0 = no limit, kept as chosen
        Assert.Equal(new Placement("game/X", 10, -20), Assert.Single(back.Placements));
    }

    [Fact]
    public void SaveLoad_EmptyHotkeys_StayEmpty()
    {
        var p = PathOf("hk.json");
        new AppConfig { LockHotkey = "", QuickSwitchHotkey = "" }.Save(p);
        var back = AppConfig.Load(p);
        Assert.Equal("", back.LockHotkey);       // "not used", not the default Scroll Lock
        Assert.Equal("", back.QuickSwitchHotkey);
    }

    [Fact]
    public void Defaults_SpeedIs30() => Assert.Equal(30, new AppConfig().FileSpeedLimitMBps);

    [Fact]
    public void Load_OldFileWithoutSpeed_Uses30()
    {
        var p = PathOf("old.json");
        File.WriteAllText(p, """{ "Role": "Receiver", "FileSizeLimitMB": 800 }""");
        var back = AppConfig.Load(p);
        Assert.Equal(800, back.FileSizeLimitMB);
        Assert.Equal(30, back.FileSpeedLimitMBps); // not 0, which would mean no limit
    }

    [Fact]
    public void Load_Corrupt_ReturnsDefaultsAndBacksUp()
    {
        var p = PathOf("config.json");
        File.WriteAllText(p, "{ not json");
        var c = AppConfig.Load(p);
        Assert.Equal(24850, c.Port);
        Assert.True(File.Exists(p + ".bak"));
        Assert.False(File.Exists(p));
    }
}
