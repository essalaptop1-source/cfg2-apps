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

    /// <summary>Account balance read from the key line (owner sets `|balance:NN` on the key).</summary>
    public static double Balance { get; private set; }
    /// <summary>True when the key line carries an explicit balance marker.</summary>
    public static bool HasBalance { get; private set; }

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
                var key = raw.Split('\n').LastOrDefault()?.Trim() ?? "";
                RefreshBalance(key);
            }
        }
        catch { }
    }

    private static void RefreshBalance(string key)
    {
        Balance = 0;
        HasBalance = false;
        if (string.IsNullOrEmpty(key) || !File.Exists(KeysPath)) return;
        try
        {
            foreach (var line in File.ReadAllLines(KeysPath))
            {
                var parts = line.Split('|');
                if (parts.Length > 0 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    ParseBalance(line);
                    return;
                }
            }
        }
        catch { }
    }

    private static void ParseBalance(string line)
    {
        Balance = 0;
        HasBalance = false;
        foreach (var p in line.Split('|'))
        {
            var kv = p.Trim().Split(':');
            if (kv.Length == 2 && kv[0].Trim().Equals("balance", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(kv[1], System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                Balance = v;
                HasBalance = true;
                return;
            }
        }
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

            // A key line may be: key | key|hwid|ip | key|hwid|ip|balance:N
            // Anything after the key that isn't a balance marker means the key
            // has been claimed by a device, so only balance markers = unbound.
            var boundHere = false;
            var claimedElsewhere = false;
            foreach (var p in parts.Skip(1))
            {
                var pv = p.Trim();
                if (pv.Equals(hwid, StringComparison.Ordinal)) { boundHere = true; break; }
                if (!pv.StartsWith("balance", StringComparison.OrdinalIgnoreCase)) claimedElsewhere = true;
            }

            if (boundHere)
            {
                IsPremiumActive = true;
                ParseBalance(line);
                SaveState(key, hwid, ip);
                return (true, "Premium unlocked! Key already bound to this device.");
            }

            if (!claimedElsewhere)
            {
                // Unbound - claim it for this device. Keep any balance suffix.
                var balanceSeg = "";
                foreach (var p in parts.Skip(1))
                    if (p.Trim().StartsWith("balance", StringComparison.OrdinalIgnoreCase))
                    {
                        balanceSeg = "|" + p.Trim();
                        break;
                    }
                lines[i] = $"{key}|{hwid}|{ip}{balanceSeg}";
                try
                {
                    File.WriteAllLines(path, lines);
                }
                catch { return (false, "Could not write keys.txt (read-only?)."); }
                IsPremiumActive = true;
                ParseBalance(lines[i]);
                SaveState(key, hwid, ip);
                return (true, $"Premium unlocked! Bound to this device (HWID {hwid[..8]}…, IP {ip}).");
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
