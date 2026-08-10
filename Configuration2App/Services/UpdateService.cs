using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Configuration2App.Models;
using Newtonsoft.Json.Linq;

namespace Configuration2App.Services;

public record UpdateInfo(string Version, string DownloadUrl, string? Notes);

/// <summary>
/// Checks for newer versions and replaces the running executable.
/// Two update sources are supported (either works, GitHub takes priority):
///  - GitHub releases:  settings.GitHubRepo = "owner/repo" — the latest release's
///    tag must be a version (v1.2.0) and its asset named like the running exe.
///  - version.json URL: settings.UpdateUrl points at a static JSON file:
///    { "version": "1.2.0", "url": "https://host/CFG2 Embed sender.exe", "notes": "..." }
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CFG2-Embed-Sender-Updater/1.0");
        return client;
    }

    public static Version LocalVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Returns update info when a newer version exists, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(AppSettings settings)
    {
        var repo = settings.GitHubRepo?.Trim();
        if (!string.IsNullOrWhiteSpace(repo))
        {
            try
            {
                var url = $"https://api.github.com/repos/{repo}/releases/latest";
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                var version = ParseVersion((string?)json["tag_name"]);
                var exeName = Path.GetFileName(Environment.ProcessPath ?? "");
                var assets = json["assets"] as JArray;
                var asset = assets?.FirstOrDefault(a =>
                               string.Equals((string?)a["name"], exeName, StringComparison.OrdinalIgnoreCase))
                           ?? assets?.FirstOrDefault();
                var downloadUrl = (string?)asset?["browser_download_url"];
                if (version == null || string.IsNullOrWhiteSpace(downloadUrl)) return null;
                return version > LocalVersion
                    ? new UpdateInfo(version.ToString(3), downloadUrl, (string?)json["body"])
                    : null;
            }
            catch
            {
                return null; // offline or repo not found — never block the app
            }
        }

        var updateUrl = settings.UpdateUrl?.Trim();
        if (string.IsNullOrWhiteSpace(updateUrl)) return null;
        try
        {
            using var resp = await Http.GetAsync(updateUrl).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var version = ParseVersion((string?)json["version"]);
            var downloadUrl = (string?)json["url"];
            if (version == null || string.IsNullOrWhiteSpace(downloadUrl)) return null;
            return version > LocalVersion
                ? new UpdateInfo(version.ToString(3), downloadUrl, (string?)json["notes"])
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the update next to the running exe and hands off to a hidden batch
    /// script that waits for this process to exit, swaps the file and relaunches.
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

    public static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim().TrimStart('v', 'V');
        return Version.TryParse(raw, out var v) ? v : null;
    }
}
