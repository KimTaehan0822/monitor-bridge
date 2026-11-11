using System.Diagnostics;
using System.Security.Principal;
using MonitorBridge.Shared;

namespace MonitorBridge.UI;

/// <summary>
/// "Run at logon with highest privileges" via Task Scheduler. XML is used because the schtasks defaults
/// would kill the app after 72 hours and skip it on battery power.
/// </summary>
internal static class AutoStart
{
    const string TaskName = "MonitorBridge";

    public static bool IsEnabled() => Run("schtasks", $"/Query /TN {TaskName}") == 0;

    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Applies the setting, asking for elevation (UAC) when needed. Returns false if it could not be applied.</summary>
    public static bool Set(bool enabled)
    {
        if (enabled == IsEnabled()) return true;
        if (!IsAdmin())
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--autostart {(enabled ? "on" : "off")}")
                    { UseShellExecute = true, Verb = "runas" });
                p?.WaitForExit();
                return p?.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // user declined UAC
        }
        return ApplyElevated(enabled);
    }

    /// <summary>Runs inside the elevated helper process.</summary>
    public static bool ApplyElevated(bool enabled)
    {
        if (!enabled) return Run("schtasks", $"/Delete /TN {TaskName} /F") == 0;
        var user = WindowsIdentity.GetCurrent().Name;
        var exe = Environment.ProcessPath!;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Escape(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Escape(exe)}</Command></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "MonitorBridge-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try { return Run("schtasks", $"/Create /TN {TaskName} /XML \"{file}\" /F") == 0; }
        finally { File.Delete(file); }
    }

    static string Escape(string s) => System.Security.SecurityElement.Escape(s);

    static int Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit();
            if (p.ExitCode != 0) Log.Info($"{exe} {args} -> {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
            return p.ExitCode;
        }
        catch (Exception e) { Log.Error(e); return -1; }
    }
}
