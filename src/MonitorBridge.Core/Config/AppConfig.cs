using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>Stream PC: hide its cursor while the game PC's cursor is elsewhere, so it stays out of captures.</summary>
    public bool HideReceiverCursor { get; set; } = true;
    /// <summary>Password sealed on disk, 8+ characters, lockout after wrong tries. Both PCs must match.</summary>
    public bool EnhancedSecurity { get; set; }
    public const int EnhancedMinPassword = 8;
    /// <summary>The sealed password could not be opened here (config copied from another PC or account).</summary>
    [JsonIgnore] public bool PasswordUnreadable { get; private set; }

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
            var node = JsonNode.Parse(File.ReadAllText(path));
            var cfg = node.Deserialize<AppConfig>(Json) ?? new AppConfig();
            if (node?["ProtectedPassword"]?.GetValue<string>() is { Length: > 0 } sealedText)
            {
                var plain = PasswordVault.TryUnprotect(sealedText);
                cfg.Password = plain ?? "";
                cfg.PasswordUnreadable = plain is null;
            }
            return cfg;
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
        var node = JsonSerializer.SerializeToNode(this, Json)!;
        if (EnhancedSecurity)
        {
            node["Password"] = ""; // older builds read this one; empty makes them ask for the password
            node["ProtectedPassword"] = PasswordVault.Protect(Password);
        }
        File.WriteAllText(tmp, node.ToJsonString(Json));
        File.Move(tmp, path, overwrite: true);
    }
}
