using System.Text;
using MonitorBridge.Native;
using MonitorBridge.Receiver;
using MonitorBridge.Shared;

namespace MonitorBridge;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        CursorHider.Restore();
        if (args.Contains("--selftest")) { SelfTest(); return; }
    }

    /// <summary>Hardware checks that unit tests cannot do. Writes %TEMP%\MonitorBridge-selftest.txt.</summary>
    static void SelfTest()
    {
        var log = new StringBuilder();
        var monitors = MonitorEnumerator.GetMonitors();
        foreach (var m in monitors) log.AppendLine($"monitor {m.Key} {m.Width}x{m.Height} at {m.OsX},{m.OsY} scale {m.Scale}");

        Win32.GetCursorPos(out var start);
        var inj = new InputInjector();
        foreach (var m in monitors)
        {
            int x = m.OsX + m.Width / 2, y = m.OsY + m.Height / 2;
            inj.MoveTo(x, y);
            Win32.GetCursorPos(out var p);
            log.AppendLine($"moveto {x},{y} -> {p.X},{p.Y} {(p.X == x && p.Y == y ? "OK" : "FAIL")}");
        }
        Win32.SetCursorPos(start.X, start.Y);

        using (var form = new Form { Text = "MonitorBridge selftest", Width = 300, Height = 100, StartPosition = FormStartPosition.CenterScreen, TopMost = true })
        {
            var box = new TextBox { Dock = DockStyle.Fill };
            form.Controls.Add(box);
            form.Show();
            form.Activate();
            box.Focus();
            Application.DoEvents();
            foreach (ushort sc in new ushort[] { 0x1E, 0x30 }) { inj.Key(sc, false, true); inj.Key(sc, false, false); }
            for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(10); }
            log.AppendLine($"keys typed '{box.Text}' {(box.Text == "ab" ? "OK" : "FAIL")}");
        }

        CursorHider.Hide();
        log.AppendLine($"cursor hidden {CursorHider.IsHidden}");
        Thread.Sleep(1000);
        CursorHider.Restore();
        log.AppendLine($"cursor restored {!CursorHider.IsHidden}");

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "MonitorBridge-selftest.txt"), log.ToString());
    }
}
