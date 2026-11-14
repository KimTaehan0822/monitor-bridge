using MonitorBridge.Core.Config;
using MonitorBridge.Shared;

namespace MonitorBridge.Core.Tests;

public class AdminNoticeTests
{
    const string ButtonHint = "아래 버튼을 누르면 관리자 권한으로 다시 켜요. 매번 자동으로 관리자 권한으로 켜려면 위 자동 실행을 켜 두세요.";

    [Fact]
    public void Balloon_Host_TalksAboutGames() => Assert.Equal(
        "관리자 권한 없이 실행 중이라, 관리자 권한으로 도는 게임·프로그램 화면에서는 단축키와 커서 이동이 동작하지 않아요. 이 알림을 누르면 관리자 권한으로 다시 켜요.",
        AdminNotice.Balloon(Role.Host));

    [Fact]
    public void Balloon_Receiver_TalksAboutInstallers() => Assert.Equal(
        "관리자 권한 없이 실행 중이라 설치·제거 창, 관리자로 켠 OBS 같은 프로그램은 조작할 수 없어요. 이 알림을 누르면 관리자 권한으로 다시 켜요.",
        AdminNotice.Balloon(Role.Receiver));

    [Theory]
    [InlineData(Role.Host)]
    [InlineData(Role.Receiver)]
    public void Settings_EndsWithButtonHint(Role role)
    {
        var s = AdminNotice.Settings(role);
        Assert.EndsWith(ButtonHint, s);
        Assert.DoesNotContain("이 알림을 누르면", s);
    }

    [Fact]
    public void WaitForPid_Reads()
    {
        Assert.Equal(1234, AdminNotice.WaitForPid(new[] { "--wait-for", "1234" }));
        Assert.Null(AdminNotice.WaitForPid(new[] { "--selftest" }));
        Assert.Null(AdminNotice.WaitForPid(new[] { "--wait-for", "abc" }));
        Assert.Null(AdminNotice.WaitForPid(new[] { "--wait-for" }));
    }
}
