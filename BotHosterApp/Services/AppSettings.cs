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
    public bool LaunchOnStartup { get; set; }
    public bool KeepInTray { get; set; } = true;

    private const string StartupValueName = "CFG2BotHoster";

    /// <summary>Registers (or removes) the app in the current user's startup
    /// programs via the HKCU Run key. The --tray flag starts it hidden in the
    /// background so the bots keep running without a window on the desktop.</summary>
    public static void SetLaunchOnStartup(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0)
                    key.SetValue(StartupValueName, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }
        }
        catch { }
    }

    public static bool IsLaunchOnStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(StartupValueName) != null;
        }
        catch
        {
            return false;
        }
    }

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
