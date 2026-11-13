using System.Text.Json;
using System.Text.Json.Serialization;
using MonitorBridge.Core.Layout;

namespace MonitorBridge.Core.Config;

public enum Role { Host, Receiver }

public sealed class AppConfig
{
    public Role Role { get; set; } = Role.Host;
    public string Password { get; set; } = "";
    public string ReceiverAddress { get; set; } = "";
    public int Port { get; set; } = 24850;
    public string LockHotkey { get; set; } = "Scroll";
    public string QuickSwitchHotkey { get; set; } = "Ctrl+Alt+Oem3";
    public int FileSizeLimitMB { get; set; } = 500;
    /// <summary>Most MB/s this PC sends files at; 0 = no limit. Keeps room on the LAN for the broadcast upload.</summary>
    public int FileSpeedLimitMBps { get; set; } = 30;
    public bool AutoStart { get; set; }
    public List<Placement> Placements { get; set; } = new();

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorBridge", "config.json");

    /// <summary>Missing file gives defaults; a corrupt file is moved to .bak and defaults are returned.</summary>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json) ?? new AppConfig();
        }
        catch (JsonException)
        {
            File.Move(path, path + ".bak", overwrite: true);
            return new AppConfig();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
