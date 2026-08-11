using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BotHosterApp.Services;

/// <summary>
/// Premium licensing: keys live in keys.txt next to the exe, one per line.
///   XXXX-XXXX-XXXX-XXXX                  (unbound)
///   XXXX-XXXX-XXXX-XXXX|HWID|IP          (claimed by a device)
/// Activating binds the key to this machine's HWID + public IP; a bound key
/// only works on the device that claimed it. Deleting a key's line revokes it.
/// </summary>
public static class LicenseService
{
    private static readonly Regex KeyRx = new("^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}$");
    private static string KeysPath => AppPaths.Combine("keys.txt");
    private static string StatePath => Path.Combine(AppPaths.LocalDataDir, "bot_hoster_license.json");

    public static bool IsPremiumActive { get; private set; }

    /// <summary>Loads cached premium state so the UI is right at startup.</summary>
    public static void RefreshStatus()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var raw = File.ReadAllText(StatePath).Trim();
                // The state line is "premium:<hwid>"; IPs change, the HWID is stable.
                IsPremiumActive = raw.StartsWith("premium:" + HwId());
            }
        }
        catch { }
    }

    private static string IpCache { get; set; } = "";

    public static bool IsValidKeyFormat(string key) =>
        !string.IsNullOrWhiteSpace(key) && KeyRx.IsMatch(key.Trim().ToUpperInvariant());

    /// <summary>Validates the key, binds it to this device, returns (ok, message).</summary>
    public static async Task<(bool Ok, string Msg)> TryActivateAsync(string rawKey)
    {
        var key = rawKey.Trim().ToUpperInvariant();
        if (!IsValidKeyFormat(key)) return (false, "Invalid key format - expected XXXX-XXXX-XXXX-XXXX.");

        string path;
        try { path = KeysPath; }
        catch { return (false, "Cannot locate keys.txt - is it next to the exe?"); }

        if (!File.Exists(path)) return (false, "keys.txt not found next to the app.");

        var hwid = HwId();
        var ip = await GetPublicIpAsync();

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return (false, "Cannot read keys.txt."); }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('|');
            if (parts[0].Trim().ToUpperInvariant() != key) continue;

            if (parts.Length == 1)
            {
                // Unbound - claim it for this device.
                lines[i] = $"{key}|{hwid}|{ip}";
                try
                {
                    File.WriteAllLines(path, lines);
                }
                catch { return (false, "Could not write keys.txt (read-only?)."); }
                IsPremiumActive = true;
                SaveState(key, hwid, ip);
                return (true, $"Premium unlocked! Bound to this device (HWID {hwid[..8]}…, IP {ip}).");
            }

            if (parts.Length >= 2 && parts[1].Trim() == hwid)
            {
                IsPremiumActive = true;
                SaveState(key, hwid, ip);
                return (true, "Premium unlocked! Key already bound to this device.");
            }

            return (false, "Key already used on another device.");
        }

        return (false, "Key not found in keys.txt.");
    }

    private static void SaveState(string key, string hwid, string ip)
    {
        try
        {
            File.WriteAllText(StatePath, $"premium:{hwid}:{ip}\n{key}");
        }
        catch { }
    }

    public static async Task<string> GetPublicIpAsync()
    {
        if (!string.IsNullOrEmpty(IpCache)) return IpCache;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            IpCache = (await client.GetStringAsync("https://api.ipify.org")).Trim();
        }
        catch { IpCache = "unknown"; }
        return IpCache;
    }

    /// <summary>Stable per-machine identifier (Windows MachineGuid).</summary>
    public static string HwId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrEmpty(guid)) return guid.Replace("-", "").ToUpperInvariant();
        }
        catch { }
        try
        {
            return Environment.MachineName.ToUpperInvariant() + Environment.UserName.ToUpperInvariant();
        }
        catch { return "UNKNOWN"; }
    }
}
