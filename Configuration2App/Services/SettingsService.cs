using System.IO;
using Configuration2App.Models;
using Newtonsoft.Json;

namespace Configuration2App.Services;

public static class SettingsService
{
    public static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kicia");

    public static readonly string SettingsPath = Path.Combine(AppDataFolder, "settings.json");

    private static AppSettings? _cache;

    public static AppSettings Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _cache = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                _cache = new AppSettings();
            }
        }
        catch
        {
            _cache = new AppSettings();
        }
        return _cache;
    }

    public static void Save(AppSettings settings)
    {
        _cache = settings;
        try
        {
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
        catch
        {
            // Best effort persistence.
        }
    }
}
