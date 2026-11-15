using System.Net;
using System.Security.Authentication;
using MonitorBridge.Core.Config;
using MonitorBridge.Core.Net;

namespace MonitorBridge.Core.Tests;

public class EnhancedSecurityTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mb-sec").FullName;
    string PathOf(string n) => Path.Combine(_dir, n);
    public void Dispose() => Directory.Delete(_dir, true);

    // --- Handshake: both PCs must agree on the mode ---

    static async Task<(Task<SecureChannel> Client, Task<SecureChannel> Server)> Handshake(string pc, bool ec, string ps, bool es)
    {
        var (c, s) = await SecureChannelTests.TcpPair();
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
        return (SecureChannel.ConnectAsync(c, pc, ct, ec), SecureChannel.AcceptAsync(s, ps, ct, es));
    }

    [Fact]
    public async Task BothEnhanced_Connects()
    {
        var (c, s) = await Handshake("password1", true, "password1", true);
        var client = await c;
        var server = await s;
        await client.SendAsync(new byte[] { 5 }, default);
        Assert.Equal(new byte[] { 5 }, await server.ReceiveAsync(default));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ModeMismatch_BothSidesSayMismatch(bool clientEnhanced, bool serverEnhanced)
    {
        var (c, s) = await Handshake("password1", clientEnhanced, "password1", serverEnhanced);
        await Assert.ThrowsAsync<SecurityModeMismatchException>(() => s);
        await Assert.ThrowsAsync<SecurityModeMismatchException>(() => c);
    }

    [Fact]
    public async Task WrongPassword_WithMismatchedMode_IsAPasswordError()
    {
        var (c, s) = await Handshake("password1", true, "password2", false);
        var e = await Assert.ThrowsAsync<AuthenticationException>(() => s);
        Assert.IsNotType<SecurityModeMismatchException>(e);
        await Assert.ThrowsAnyAsync<AuthenticationException>(() => c);
    }

    // --- Lockout after repeated wrong passwords ---

    [Fact]
    public void Limiter_BlocksAfterFiveFailures_ForThreeMinutes()
    {
        long now = 0;
        var lim = new AuthLimiter(5, TimeSpan.FromMinutes(3), () => now);
        var ip = IPAddress.Parse("192.168.0.20");
        for (int i = 0; i < 4; i++) Assert.False(lim.Fail(ip));
        Assert.False(lim.IsBlocked(ip));
        Assert.True(lim.Fail(ip)); // the fifth starts the block
        Assert.True(lim.IsBlocked(ip));
        Assert.False(lim.IsBlocked(IPAddress.Parse("192.168.0.21"))); // other PCs are not affected
        now += (long)TimeSpan.FromMinutes(3).TotalMilliseconds - 1;
        Assert.True(lim.IsBlocked(ip));
        now += 1;
        Assert.False(lim.IsBlocked(ip));
        Assert.False(lim.Fail(ip)); // count starts over after the block
    }

    [Fact]
    public void Limiter_SuccessResetsCount()
    {
        var lim = new AuthLimiter(5, TimeSpan.FromMinutes(3), () => 0);
        var ip = IPAddress.Loopback;
        for (int i = 0; i < 4; i++) lim.Fail(ip);
        lim.Succeed(ip);
        for (int i = 0; i < 4; i++) Assert.False(lim.Fail(ip));
        Assert.False(lim.IsBlocked(ip));
    }

    // --- Password stored encrypted ---

    [Fact]
    public void Vault_RoundTrips_AndRejectsGarbage()
    {
        var sealedText = PasswordVault.Protect("비밀번호123");
        Assert.DoesNotContain("123", sealedText);
        Assert.Equal("비밀번호123", PasswordVault.TryUnprotect(sealedText));
        Assert.Null(PasswordVault.TryUnprotect(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
        Assert.Null(PasswordVault.TryUnprotect("not base64!"));
    }

    [Fact]
    public void Enhanced_SavesNoPlainPassword_AndLoadsItBack()
    {
        var p = PathOf("config.json");
        new AppConfig { Password = "longsecret99", EnhancedSecurity = true }.Save(p);
        Assert.DoesNotContain("longsecret99", File.ReadAllText(p));
        var back = AppConfig.Load(p);
        Assert.True(back.EnhancedSecurity);
        Assert.Equal("longsecret99", back.Password);
        Assert.False(back.PasswordUnreadable);
    }

    [Fact]
    public void Normal_KeepsPlainPassword_ForOlderBuilds()
    {
        var p = PathOf("config.json");
        new AppConfig { Password = "pw" }.Save(p);
        Assert.Contains("\"pw\"", File.ReadAllText(p));
    }

    [Fact]
    public void Enhanced_PasswordFromAnotherPc_LoadsEmptyAndFlagged()
    {
        var p = PathOf("config.json");
        File.WriteAllText(p, "{ \"EnhancedSecurity\": true, \"Password\": \"\", \"ProtectedPassword\": \"AQIDBA==\" }");
        var back = AppConfig.Load(p);
        Assert.Equal("", back.Password);
        Assert.True(back.PasswordUnreadable);
    }
}

public class HideCursorSettingTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mb-hide").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void OlderConfig_HidesCursorByDefault_AndChoiceRoundTrips()
    {
        var p = Path.Combine(_dir, "config.json");
        File.WriteAllText(p, "{ \"Password\": \"pw\" }"); // written by a build without the setting
        Assert.True(AppConfig.Load(p).HideReceiverCursor);
        new AppConfig { Password = "pw", HideReceiverCursor = false }.Save(p);
        Assert.False(AppConfig.Load(p).HideReceiverCursor);
    }
}
