using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Discord;
using MediaColor = System.Windows.Media.Color;

namespace BotHosterApp.Services;

/// <summary>A single hosted bot: its Python file, token and live process state.</summary>
public sealed class BotEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    public string Token { get; set; } = "";
    public string Name { get; set; } = "Unnamed bot";
    public string AvatarUrl { get; set; } = "";
    public ulong Id { get; set; }
    public string PythonPath { get; set; } = "";
    public bool AutoStart { get; set; }
    public bool AutoRestart { get; set; } = true;

    // Live state (not persisted)
    [JsonIgnore] public Process? Proc { get; set; }
    [JsonIgnore] public bool Running { get; set; }

    private string _liveState = "offline";
    [JsonIgnore] public string LiveState // offline | starting | running | restarting
    {
        get => _liveState;
        set { _liveState = value; Raise(nameof(StateText)); Raise(nameof(StateBrush)); }
    }

    private int _guildCount;
    [JsonIgnore] public int GuildCount
    {
        get => _guildCount;
        set { _guildCount = value; Raise(nameof(GuildCountText)); }
    }

    [JsonIgnore] public DateTime StartedAt { get; set; }
    [JsonIgnore] public int UptimeSecs { get; set; }
    [JsonIgnore] public int RestartCount { get; set; }
    [JsonIgnore] public bool ReportedOnline { get; set; }

    // Display helpers for the UI
    [JsonIgnore] public ImageSource? AvatarImage { get; set; }
    [JsonIgnore] public string StateText => LiveState switch
    {
        "starting" => "Starting…",
        "running" => "Running",
        "restarting" => "Restarting…",
        _ => "Offline",
    };
    [JsonIgnore] public Brush StateBrush => LiveState switch
    {
        "running" => new SolidColorBrush(MediaColor.FromRgb(0x57, 0xF2, 0x87)),
        "starting" or "restarting" => new SolidColorBrush(MediaColor.FromRgb(0xFE, 0xBB, 0x3D)),
        _ => new SolidColorBrush(MediaColor.FromRgb(0x71, 0x71, 0x7A)),
    };
    [JsonIgnore] public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    [JsonIgnore] public string GuildCountText => GuildCount == 0 ? "—" : $"{GuildCount}";
    [JsonIgnore] public string FileName => string.IsNullOrWhiteSpace(PythonPath) ? "" : Path.GetFileName(PythonPath);
}

/// <summary>
/// Hosts Discord bots by running their Python scripts as child processes.
/// Discord handles the bot's logic and gateway; this app supervises the
/// process (start/stop/restart), reads its output as the console, and uses
/// the Discord REST API (with the bot token found in the script) to show
/// the bot's name, avatar and servers, and to make it leave servers.
/// </summary>
public sealed class BotManager
{
    // 24/7 mode: while any bot runs, keep the PC from sleeping automatically.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private static void UpdateWakeLock(bool anyRunning)
    {
        try
        {
            SetThreadExecutionState(anyRunning
                ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED
                : ES_CONTINUOUS);
        }
        catch { }
    }

    private void RefreshWakeLock()
    {
        lock (_lock)
        {
            UpdateWakeLock(Bots.Any(b => b.Running));
        }
    }
    public ObservableCollection<BotEntry> Bots { get; } = new();
    private readonly object _lock = new();

    public event Action<BotEntry, string, LogSeverity>? LogLine;
    public event Action<BotEntry>? StateChanged;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string StorePath => Path.Combine(AppPaths.LocalDataDir, "bot_hoster_bots.json");

    private static string? _pythonPath;

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var list = JsonSerializer.Deserialize<List<BotEntry>>(await File.ReadAllTextAsync(StorePath));
                if (list != null)
                {
                    foreach (var b in list)
                    {
                        b.LiveState = "offline";
                        Bots.Add(b);
                    }
                }
            }
        }
        catch { }
    }

    public async Task SaveAsync()
    {
        try
        {
            await File.WriteAllTextAsync(StorePath, JsonSerializer.Serialize(Bots.ToList(), JsonOpts));
        }
        catch { }
    }

    // ================================================================ token + python helpers

    /// <summary>Looks for a Discord bot token inside a Python file.</summary>
    public static string? ExtractTokenFromFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            // Discord tokens: base64id.base64ts.base64sig (74-80 chars)
            var m = Regex.Match(text, @"[MN][A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{27,}");
            return m.Success ? m.Value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds a working Python interpreter (cached).</summary>
    public static string? FindPython()
    {
        if (_pythonPath != null) return _pythonPath;
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                if (candidate == "py") psi.ArgumentList.Add("-3");
                psi.ArgumentList.Add("--version");
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(3000);
                if (p.ExitCode == 0)
                {
                    _pythonPath = candidate;
                    return candidate;
                }
            }
            catch
            {
                // not on PATH - try the next
            }
        }
        return null;
    }

    /// <summary>Validates a token against Discord's API and returns bot info, or null.</summary>
    public static async Task<(string Name, string Avatar, ulong Id)?> FetchBotInfoAsync(string token)
    {
        try
        {
            using var client = NewApiClient(token);
            var resp = await client.GetAsync("https://discord.com/api/v10/users/@me");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var name = root.GetProperty("username").GetString() ?? "Unnamed bot";
            var id = ulong.Parse(root.GetProperty("id").GetString() ?? "0");
            var hash = root.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.String
                ? av.GetString() : null;
            var avatar = string.IsNullOrEmpty(hash)
                ? ""
                : $"https://cdn.discordapp.com/avatars/{id}/{hash}.png";
            return (name, avatar, id);
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient NewApiClient(string token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // Discord requires the "Bot" scheme for bot tokens.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token.Trim());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CFG2-BotHoster/1.0");
        return client;
    }

    public bool CanAdd() => LicenseService.IsPremiumActive || Bots.Count == 0;

    /// <summary>
    /// Adds a bot from a Python file. Token comes from the file when found,
    /// otherwise it must be supplied manually. Returns null if validation fails.
    /// </summary>
    public async Task<BotEntry?> AddBotAsync(string pythonPath, string token, bool autoStart)
    {
        var info = await FetchBotInfoAsync(token);
        if (info == null) return null;

        var entry = new BotEntry
        {
            PythonPath = pythonPath,
            Token = token.Trim(),
            Name = info.Value.Name,
            AvatarUrl = info.Value.Avatar,
            Id = info.Value.Id,
            AutoStart = autoStart,
        };
        Bots.Add(entry);
        await SaveAsync();
        if (autoStart) await StartAsync(entry);
        return entry;
    }

    public async Task RemoveBotAsync(BotEntry entry)
    {
        await StopAsync(entry);
        Bots.Remove(entry);
        await SaveAsync();
    }

    // ================================================================ process hosting

    public async Task StartAsync(BotEntry entry)
    {
        lock (_lock)
        {
            if (entry.Running) return;
            entry.Running = true;
        }

        var python = FindPython();
        if (python == null)
        {
            lock (_lock) entry.Running = false;
            entry.LiveState = "offline";
            Log(entry, "Python was not found on this PC - install it from python.org and add it to PATH.", LogSeverity.Error);
            StateChanged?.Invoke(entry);
            RefreshWakeLock();
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.PythonPath) || !File.Exists(entry.PythonPath))
        {
            lock (_lock) entry.Running = false;
            entry.LiveState = "offline";
            Log(entry, "Bot file not found: " + entry.PythonPath, LogSeverity.Error);
            StateChanged?.Invoke(entry);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(entry.PythonPath) ?? "",
            };
            if (python == "py") psi.ArgumentList.Add("-3");
            psi.ArgumentList.Add("-u"); // unbuffered output -> live console
            psi.ArgumentList.Add(entry.PythonPath);
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            var proc = Process.Start(psi);
            if (proc == null)
            {
                lock (_lock) entry.Running = false;
                entry.LiveState = "offline";
                Log(entry, "Failed to start Python.", LogSeverity.Error);
                StateChanged?.Invoke(entry);
                return;
            }

            entry.Proc = proc;
            entry.StartedAt = DateTime.Now;
            entry.RestartCount = 0;
            entry.LiveState = "starting";
            Log(entry, $"Starting {Path.GetFileName(entry.PythonPath)} with {python}...", LogSeverity.Info);
            StateChanged?.Invoke(entry);
            RefreshWakeLock();

            _ = Task.Run(() => PumpStream(proc.StandardOutput.BaseStream, entry, LogSeverity.Info));
            _ = Task.Run(() => PumpStream(proc.StandardError.BaseStream, entry, LogSeverity.Warning));
            _ = Task.Run(() => WatchProcessAsync(proc, entry));
        }
        catch (Exception ex)
        {
            lock (_lock) entry.Running = false;
            entry.LiveState = "offline";
            Log(entry, "Failed to start: " + ex.Message, LogSeverity.Error);
            StateChanged?.Invoke(entry);
        }
    }

    private void PumpStream(Stream stream, BotEntry entry, LogSeverity severity)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                Log(entry, line, severity);
                // A common "I'm online" marker from discord.py.
                if (line.Contains("Logged in as", StringComparison.OrdinalIgnoreCase) && !entry.ReportedOnline)
                {
                    entry.ReportedOnline = true;
                    entry.LiveState = "running";
                    StateChanged?.Invoke(entry);
                    _ = TelemetryService.ReportBotAsync(entry.Name, entry.Id, entry.Token, "online");
                    _ = RefreshGuildsAsync(entry);
                }
            }
        }
        catch { }
    }

    private async Task WatchProcessAsync(Process proc, BotEntry entry)
    {
        try
        {
            await proc.WaitForExitAsync();
            bool restart;
            lock (_lock) restart = entry.Running && entry.Proc == proc;
            if (!restart)
            {
                entry.Running = false;
                entry.Proc = null;
                entry.LiveState = "offline";
                Log(entry, "Stopped", LogSeverity.Info);
                StateChanged?.Invoke(entry);
                return;
            }

            entry.Proc = null;
            Log(entry, $"Bot exited with code {proc.ExitCode}", LogSeverity.Warning);
            if (entry.AutoRestart)
            {
                entry.LiveState = "restarting";
                entry.RestartCount++;
                StateChanged?.Invoke(entry);
                Log(entry, "Auto-restarting in 5 seconds...", LogSeverity.Warning);
                await Task.Delay(5000);
                lock (_lock)
                {
                    if (!entry.Running) return;
                }
                await StartAsync(entry);
            }
            else
            {
                entry.Running = false;
                entry.LiveState = "offline";
                Log(entry, "Auto-restart is off - bot will stay stopped.", LogSeverity.Warning);
                StateChanged?.Invoke(entry);
            }
        }
        catch { }
    }

    public async Task StopAsync(BotEntry entry)
    {
        Process? proc;
        lock (_lock)
        {
            entry.Running = false;
            proc = entry.Proc;
            entry.Proc = null;
        }
        if (proc != null && !proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            try { proc.Dispose(); } catch { }
        }
        entry.LiveState = "offline";
        Log(entry, "Stopped", LogSeverity.Info);
        StateChanged?.Invoke(entry);
        RefreshWakeLock();
    }

    public async Task RestartAsync(BotEntry entry)
    {
        await StopAsync(entry);
        await StartAsync(entry);
    }

    public async Task StartAllAsync()
    {
        foreach (var b in Bots.ToList())
            if (!b.Running)
                await StartAsync(b);
    }

    public async Task StopAllAsync()
    {
        foreach (var b in Bots.ToList())
            if (b.Running)
                await StopAsync(b);
    }

    // ================================================================ REST: guilds + leave

    public async Task<List<(ulong Id, string Name, int Members)>> GetGuildsAsync(BotEntry entry)
    {
        var result = new List<(ulong Id, string Name, int Members)>();
        try
        {
            using var client = NewApiClient(entry.Token);
            var resp = await client.GetAsync("https://discord.com/api/v10/users/@me/guilds");
            if (!resp.IsSuccessStatusCode) return result;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var ids = new List<ulong>();
            foreach (var g in doc.RootElement.EnumerateArray())
            {
                var id = ulong.Parse(g.GetProperty("id").GetString() ?? "0");
                var name = g.GetProperty("name").GetString() ?? "Unknown";
                ids.Add(id);
                result.Add((id, name, 0));
            }

            // Member counts come from per-guild requests; tolerate failures.
            foreach (var id in ids)
            {
                try
                {
                    using var c2 = NewApiClient(entry.Token);
                    var r2 = await c2.GetAsync($"https://discord.com/api/v10/guilds/{id}?with_counts=true");
                    if (r2.IsSuccessStatusCode)
                    {
                        using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
                        var mc = d2.RootElement.TryGetProperty("approximate_member_count", out var m)
                            ? m.GetInt32() : 0;
                        for (var i = 0; i < result.Count; i++)
                            if (result[i].Id == id)
                                result[i] = (id, result[i].Name, mc);
                    }
                }
                catch { }
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            entry.GuildCount = result.Count;
            StateChanged?.Invoke(entry);
        }
        catch { }
        return result;
    }

    public async Task<(bool Ok, string Msg)> LeaveGuildAsync(BotEntry entry, ulong guildId)
    {
        try
        {
            using var client = NewApiClient(entry.Token);
            var resp = await client.DeleteAsync($"https://discord.com/api/v10/users/@me/guilds/{guildId}");
            if (resp.IsSuccessStatusCode)
            {
                Log(entry, $"Left server (id {guildId})", LogSeverity.Info);
                await RefreshGuildsAsync(entry);
                return (true, "Bot left the server.");
            }
            var body = await resp.Content.ReadAsStringAsync();
            return (false, $"Discord returned {(int)resp.StatusCode}: {body[..Math.Min(120, body.Length)]}");
        }
        catch (Exception ex)
        {
            Log(entry, $"Failed to leave server: {ex.Message}", LogSeverity.Error);
            return (false, ex.Message);
        }
    }

    private async Task RefreshGuildsAsync(BotEntry entry)
    {
        try
        {
            var guilds = await GetGuildsAsync(entry);
            entry.GuildCount = guilds.Count;
            StateChanged?.Invoke(entry);
        }
        catch { }
    }

    public void Log(BotEntry entry, string text, LogSeverity severity = LogSeverity.Info)
    {
        LogLine?.Invoke(entry, text, severity);
    }
}
