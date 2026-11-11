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
        int auto = Array.IndexOf(args, "--autostart");
        if (auto >= 0 && auto + 1 < args.Length) { Environment.Exit(UI.AutoStart.ApplyElevated(args[auto + 1] == "on") ? 0 : 1); return; }
        int render = Array.IndexOf(args, "--render-settings");
        if (render >= 0 && render + 1 < args.Length) { RenderSettings(args[render + 1]); return; }

        using var mutex = new Mutex(true, "MonitorBridge.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("Monitor Bridge가 이미 실행 중이에요. 작업 표시줄 오른쪽 아이콘을 확인해 주세요.", "Monitor Bridge");
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { CursorHider.Restore(); if (e.ExceptionObject is Exception ex) Log.Error(ex); };
        Application.ThreadException += (_, e) => Log.Error(e.Exception);
        Application.ApplicationExit += (_, _) => CursorHider.Restore();
        Application.Run(new UI.TrayContext(Core.Config.AppConfig.Load(Core.Config.AppConfig.DefaultPath)));
    }

    /// <summary>Renders the settings window with a sample layout to a PNG (for visual review).</summary>
    static void RenderSettings(string path)
    {
        var game = new Core.Layout.MonitorInfo("game", @"\\.\DISPLAY1", 2560, 1440, 0, 0, 1.25);
        var s1 = new Core.Layout.MonitorInfo("stream", @"\\.\DISPLAY1", 1920, 1080, 0, 0, 1);
        var s2 = new Core.Layout.MonitorInfo("stream", @"\\.\DISPLAY2", 1920, 1080, 1920, 0, 1);
        var layout = new Core.Layout.VirtualLayout(new[] { s1, game, s2 }, new[]
        {
            new Core.Layout.Placement(s1.Key, 0, 180), new Core.Layout.Placement(game.Key, 1920, 0), new Core.Layout.Placement(s2.Key, 4480, 180),
        });
        var cfg = new Core.Config.AppConfig { Password = "x", ReceiverAddress = "192.168.0.10" };
        using var form = new UI.SettingsForm(cfg, layout, "game", remoteConnected: true) { StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) };
        form.Show();
        Application.DoEvents();
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        bmp.Save(path);
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
