using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FPSBoosterApp.Services;

/// <summary>
/// Premium licensing. Keys live in a plain text file (keys.txt) next to the exe.
/// A key is bound on first activation to the machine's hardware ID and its
/// public IP, and will only ever work on that device + IP combo.
/// </summary>
public static class LicenseService
{
    public static string KeysPath => AppPaths.Combine("keys.txt");

    private static readonly Regex KeyPattern = new(@"^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CFG2-Recorder-License/1.0");
        return client;
    }

    /// <summary>Stable per-device identifier (hash of the Windows MachineGuid).</summary>
    public static string HardwareId
    {
        get
        {
            try
            {
                var guid = Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")
                    ?.GetValue("MachineGuid") as string ?? "unknown-machine";
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(guid)))[..16];
            }
            catch
            {
                return "unknown";
            }
        }
    }

    public static bool IsPremiumActive { get; private set; }

    /// <summary>Re-checks the keys file: premium is active when any key is bound to this device + IP.</summary>
    public static void RefreshStatus()
    {
        IsPremiumActive = false;
        try
        {
            if (!File.Exists(KeysPath)) return;
            foreach (var line in File.ReadAllLines(KeysPath))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    string.Equals(parts[1], HardwareId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(parts[2], GetPublicIp(), StringComparison.OrdinalIgnoreCase))
                {
                    IsPremiumActive = true;
                    return;
                }
            }
        }
        catch
        {
            IsPremiumActive = false;
        }
    }

    /// <summary>Validates the key and binds it to this device + IP on first use.</summary>
    public static async Task<(bool Ok, string Message)> TryActivateAsync(string key)
    {
        var normalized = (key ?? "").Trim().ToUpperInvariant();
        if (!KeyPattern.IsMatch(normalized))
            return (false, "That does not look like a valid key (format XXXX-XXXX-XXXX-XXXX).");

        if (!File.Exists(KeysPath))
            return (false, "No keys file found next to the app. The seller must provide keys.txt.");

        var ip = GetPublicIp();
        if (string.IsNullOrWhiteSpace(ip))
            return (false, "Could not detect your IP address - you need an internet connection to activate.");

        var hwid = HardwareId;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(KeysPath);
        }
        catch
        {
            return (false, "Could not read the keys file.");
        }

        var index = -1;
        string[]? parts = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var p = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length > 0 && string.Equals(p[0], normalized, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                parts = p;
                break;
            }
        }

        if (index < 0)
            return (false, "Invalid key - not found in the license file.");

        // Key already bound?
        if (parts!.Length >= 3)
        {
            var boundHwid = parts[1];
            var boundIp = parts[2];
            if (string.Equals(boundHwid, hwid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(boundIp, ip, StringComparison.OrdinalIgnoreCase))
            {
                IsPremiumActive = true;
                return (true, "This key is already active.");
            }
            if (string.Equals(boundHwid, hwid, StringComparison.OrdinalIgnoreCase))
                return (false, "This key cannot be activated on this network.");
            return (false, "This key is already in use.");
        }

        // Unbound - bind it to this device + IP.
        try
        {
            lines[index] = $"{normalized}  {hwid}  {ip}  {DateTime.Now:yyyy-MM-dd}";
            File.WriteAllLines(KeysPath, lines);
            IsPremiumActive = true;
            return (true, "Activated! Premium is now unlocked.");
        }
        catch
        {
            return (false, "Could not write to the keys file (read-only folder?).");
        }
    }

    public static string? GetPublicIp()
    {
        try
        {
            using var resp = Http.GetAsync("https://api.ipify.org").GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            var ip = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
            return string.IsNullOrWhiteSpace(ip) ? null : ip;
        }
        catch
        {
            return null;
        }
    }
}
