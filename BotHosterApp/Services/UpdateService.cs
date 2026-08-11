using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace BotHosterApp.Services;

/// <summary>
/// Checks GitHub releases for a newer version and replaces the running exe.
///
/// Multiple CFG2 apps share one repo, so releases are tagged per app:
///   hoster-v1.0.0, embed-v1.2.0, ...
/// The check lists recent releases, keeps the ones tagged with this app's
/// prefix, and only accepts a release that ships an asset matching the
/// running exe's name - so apps can never download each other's binaries.
/// </summary>
public static class UpdateService
{
    private const string Repo = "essalaptop1-source/cfg2-apps";
    private const string TagPrefix = "hoster-v";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CFG2-Bot-Hoster-Updater/1.0");
        return client;
    }

    public static Version LocalVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public sealed record UpdateInfo(Version Version, string DownloadUrl, string? Notes);

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{Repo}/releases?per_page=50";
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var releases = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var exeName = NormalizeName(Path.GetFileName(Environment.ProcessPath ?? ""));

            UpdateInfo? best = null;
            foreach (var rel in releases.EnumerateArray())
            {
                var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : "";
                if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Version.TryParse(tag[TagPrefix.Length..].TrimStart('v', 'V'), out var version)) continue;

                if (rel.TryGetProperty("assets", out var assets))
                {
                    string? url2 = null;
                    foreach (var a in assets.EnumerateArray())
                    {
                        var assetName = NormalizeName(a.TryGetProperty("name", out var n) ? n.GetString() : "");
                        if (assetName == exeName)
                        {
                            url2 = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                            break;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(url2)) continue; // never grab another app's exe
                    var notes = rel.TryGetProperty("body", out var b) ? b.GetString() : null;
                    var candidate = new UpdateInfo(version, url2!, notes);
                    if (best == null || version > best.Version) best = candidate;
                }
            }

            return best != null && best.Version > LocalVersion ? best : null;
        }
        catch
        {
            return null; // offline - never block the app
        }
    }

    /// <summary>
    /// Downloads the update next to the exe and hands off to a hidden batch
    /// script that swaps the file once this process exits, then relaunches.
    /// </summary>
    public static async Task<bool> InstallAsync(UpdateInfo info)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        var dir = Path.GetDirectoryName(exePath)!;
        var exeName = Path.GetFileName(exePath);
        var newPath = Path.Combine(dir, exeName + ".new");
        var batPath = Path.Combine(dir, "update.bat");

        try
        {
            using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var dst = File.Create(newPath))
                await src.CopyToAsync(dst).ConfigureAwait(false);

            var bat =
                "@echo off\r\n" +
                $"set \"EXE={exeName}\"\r\n" +
                "set /a tries=0\r\n" +
                ":wait\r\n" +
                "del /f /q \"%EXE%.old\" 2>nul\r\n" +
                "ren \"%EXE%\" \"%EXE%.old\" 2>nul\r\n" +
                "if exist \"%EXE%\" goto locked\r\n" +
                "goto swap\r\n" +
                ":locked\r\n" +
                "set /a tries+=1\r\n" +
                "if %tries% geq 60 exit /b 0\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                "goto wait\r\n" +
                ":swap\r\n" +
                "move /y \"%EXE%.new\" \"%EXE%\" >nul\r\n" +
                "start \"\" \"%EXE%\"\r\n" +
                "set /a tries=0\r\n" +
                ":clean\r\n" +
                "del /f /q \"%EXE%.old\" 2>nul\r\n" +
                "if not exist \"%EXE%.old\" goto done\r\n" +
                "set /a tries+=1\r\n" +
                "if %tries% geq 10 exit /b 0\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                "goto clean\r\n" +
                ":done\r\n" +
                "del \"%~f0\"\r\n";
            await File.WriteAllTextAsync(batPath, bat).ConfigureAwait(false);

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{batPath}\"\"")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = dir,
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
            return false;
        }
    }

    private static string NormalizeName(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
