using MonitorBridge.Core.Config;

namespace MonitorBridge.Core.Tests;

public class HotkeyTests
{
    [Fact]
    public void Parse_CtrlAltBacktick()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Alt+Oem3", out var h));
        Assert.Equal(0xC0, h.VirtualKey);
        Assert.True(h.Ctrl);
        Assert.True(h.Alt);
        Assert.False(h.Shift);
        Assert.False(h.Win);
        Assert.Equal("Ctrl+Alt+Oem3", h.ToString());
    }

    [Fact]
    public void Parse_ScrollLock()
    {
        Assert.True(Hotkey.TryParse("Scroll", out var h));
        Assert.Equal(0x91, h.VirtualKey);
    }

    [Theory]
    [InlineData("Ctrl+Nope")]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    public void Parse_Invalid_False(string s) => Assert.False(Hotkey.TryParse(s, out _));

    [Fact]
    public void Matcher_Reset_ForgetsStuckModifiers()
    {
        // After Ctrl+Alt+Del the key-ups never reach the hook; plain ` must not fire the quick switch.
        Assert.True(Hotkey.TryParse("Ctrl+Alt+Oem3", out var h));
        var m = new HotkeyMatcher(h);
        m.OnKey(0xA2, true);
        m.OnKey(0xA4, true);
        m.Reset();
        Assert.False(m.OnKey(0xC0, true));
    }

    [Fact]
    public void Matcher_FiresOnlyWithModifiers()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Alt+Oem3", out var h));
        var m = new HotkeyMatcher(h);
        Assert.False(m.OnKey(0xC0, true));   // no modifiers
        m.OnKey(0xC0, false);
        m.OnKey(0xA2, true);                 // LCtrl
        m.OnKey(0xA5, true);                 // RAlt
        Assert.True(m.OnKey(0xC0, true));
        Assert.False(m.OnKey(0xC0, true));   // auto-repeat does not fire again
        m.OnKey(0xC0, false);
        m.OnKey(0xA0, true);                 // + Shift: extra modifier
        Assert.False(m.OnKey(0xC0, true));
    }
}
