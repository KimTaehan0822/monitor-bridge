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
        // Right-click menu: hand the folder to the running instance and leave. Before anything that touches cursors.
        if (PasteArgs.Folder(args) is { } pasteFolder) { Environment.Exit(Paste(pasteFolder)); return; }
        CursorHider.Restore();
        if (args.Contains("--selftest")) { SelfTest(); return; }
        int auto = Array.IndexOf(args, "--autostart");
        if (auto >= 0 && auto + 1 < args.Length) { Environment.Exit(UI.AutoStart.ApplyElevated(args[auto + 1] == "on") ? 0 : 1); return; }
        int render = Array.IndexOf(args, "--render-settings");
        if (render >= 0 && render + 1 < args.Length) { RenderSettings(args[render + 1], args.Contains("--receiver"), args.Contains("--same-limit")); return; }
        int rc = Array.IndexOf(args, "--render-confirm");
        if (rc >= 0 && rc + 1 < args.Length)
        {
            RenderOffscreen(UI.PasteConfirm.ForRender("송출컴", new Core.Protocol.FileListOffer(Guid.Empty, 3, 74_732_929_434, "2026-11-08 23-00-51.mkv"), @"D:\녹화폴더\편집용"), args[rc + 1]);
            return;
        }
        int rt = Array.IndexOf(args, "--render-transfer");
        if (rt >= 0 && rt + 1 < args.Length)
        {
            var sample = new Core.Files.TransferProgress(false, "송출컴", "2026-11-08 23-00-51.mkv", 74_732_929_434, 18_683_232_358, 165_000, Core.Files.TransferState.Running);
            RenderOffscreen(UI.TransferWindow.ForRender(() => sample), args[rt + 1]);
            return;
        }
        int ripple = Array.IndexOf(args, "--render-ripple");
        if (ripple >= 0 && ripple + 1 < args.Length) { UI.RippleWindow.RenderFrames(args[ripple + 1]); return; }

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

    static int Paste(string folder)
    {
        bool running;
        try { running = Mutex.TryOpenExisting("MonitorBridge.SingleInstance", out var m) && m is not null; m?.Dispose(); }
        catch (UnauthorizedAccessException) { running = true; } // it exists, owned by the elevated instance
        if (!running)
        {
            MessageBox.Show("Monitor Bridge가 실행 중이 아니에요. 먼저 Monitor Bridge를 실행해 주세요.", "Monitor Bridge");
            return 1;
        }
        AllowSetForegroundWindow(-1); // we got the click; let the running instance bring its question to the front
        if (PasteChannel.TrySend(PasteChannel.DefaultDir, folder)) return 0;
        MessageBox.Show("Monitor Bridge가 응답하지 않아요. 잠시 뒤 다시 해 보거나, Monitor Bridge를 다시 실행해 주세요.", "Monitor Bridge");
        return 1;
    }

    /// <summary>Renders the settings window with a sample layout to a PNG (for visual review).</summary>
    static void RenderSettings(string path, bool receiver, bool sameLimit = false)
    {
        var game = new Core.Layout.MonitorInfo("game", @"\\.\DISPLAY1", 2560, 1440, 0, 0, 1.25);
        var s1 = new Core.Layout.MonitorInfo("stream", @"\\.\DISPLAY1", 1920, 1080, 0, 0, 1);
        var s2 = new Core.Layout.MonitorInfo("stream", @"\\.\DISPLAY2", 1920, 1080, 1920, 0, 1);
        var layout = new Core.Layout.VirtualLayout(new[] { s1, game, s2 }, new[]
        {
            new Core.Layout.Placement(s1.Key, 0, 180), new Core.Layout.Placement(game.Key, 1920, 0), new Core.Layout.Placement(s2.Key, 4480, 180),
        });
        var cfg = new Core.Config.AppConfig { Password = "x", ReceiverAddress = "192.168.0.10", FileSizeLimitMB = 80000, QuickSwitchHotkey = "",
            Role = receiver ? Core.Config.Role.Receiver : Core.Config.Role.Host };
        var sample = new Core.Files.TransferProgress(false, "송출컴", "2026-11-08 23-00-51.mkv", 74_732_929_434, 18_683_232_358, 165_000, Core.Files.TransferState.Running);
        var form = new UI.SettingsForm(cfg, receiver ? null : layout, "game", remoteConnected: true, () => sameLimit ? 80000 : 500, () => sample);
        if (receiver) form.ShowLocalAddresses("이 PC의 IP: 172.30.1.5  (게임컴 설정의 '송출컴 IP'에 넣으세요)");
        RenderOffscreen(form, path);
    }

    /// <summary>Draws a window to a PNG off screen, shown without activation so it never takes focus.</summary>
    static void RenderOffscreen(Form form, string path)
    {
        using (form)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-10000, -10000);
            ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
            for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(100); } // let animations (progress bar) settle
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(path);
        }
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
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
}
