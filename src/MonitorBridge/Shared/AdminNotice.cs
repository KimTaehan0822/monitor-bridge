using MonitorBridge.Core.Config;

namespace MonitorBridge.Shared;

/// <summary>
/// What to tell people when this PC runs without admin rights: Windows then blocks our input from reaching admin
/// windows (games on the game PC, installers and admin OBS on the stream PC).
/// </summary>
internal static class AdminNotice
{
    public const string TrayLine = "권한: 관리자 아님 (일부 프로그램·게임에서 동작 안 함)";
    public const string RestartText = "관리자 권한으로 다시 켜기";
    const string HostProblem = "관리자 권한 없이 실행 중이라, 관리자 권한으로 도는 게임·프로그램 화면에서는 단축키와 커서 이동이 동작하지 않아요.";
    const string ReceiverProblem = "관리자 권한 없이 실행 중이라 설치·제거 창, 관리자로 켠 OBS 같은 프로그램은 조작할 수 없어요.";

    static string Problem(Role role) => role == Role.Host ? HostProblem : ReceiverProblem;

    public static string Balloon(Role role) => Problem(role) + " 이 알림을 누르면 관리자 권한으로 다시 켜요.";

    public static string Settings(Role role) =>
        Problem(role) + "\n아래 버튼을 누르면 관리자 권한으로 다시 켜요. 매번 자동으로 관리자 권한으로 켜려면 위 자동 실행을 켜 두세요.";

    /// <summary>The previous instance to wait for after a restart as admin (<c>--wait-for &lt;pid&gt;</c>).</summary>
    public static int? WaitForPid(string[] args)
    {
        int i = Array.IndexOf(args, "--wait-for");
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid) ? pid : null;
    }
}
