using System.IO;
using System.Text.Json;

namespace BotHosterApp.Services;

/// <summary>User preferences persisted to the app data folder.</summary>
public sealed class AppSettings
{
    public bool StartAllOnLaunch { get; set; }
    public bool AutoRestartNew { get; set; } = true;
    public string DefaultStatus { get; set; } = "online";
    public string DefaultActivity { get; set; } = "playing";
    public string DefaultActivityText { get; set; } = "";
    public bool CheckUpdates { get; set; } = true;
    public bool Telemetry { get; set; } = true;

    private static string Path_ => Path.Combine(AppPaths.LocalDataDir, "bot_hoster_settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
