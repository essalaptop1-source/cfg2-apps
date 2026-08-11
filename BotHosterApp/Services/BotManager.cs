using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Discord;
using Discord.WebSocket;
using MediaColor = System.Windows.Media.Color;

namespace BotHosterApp.Services;

/// <summary>A single hosted bot: its token, settings and live client state.</summary>
public sealed class BotEntry
{
    public string Token { get; set; } = "";
    public string Name { get; set; } = "Unnamed bot";
    public string AvatarUrl { get; set; } = "";
    public ulong Id { get; set; }
    public bool AutoStart { get; set; }          // start when the app launches
    public bool AutoRestart { get; set; } = true; // restart after a crash/disconnect
    public string Status { get; set; } = "online"; // online | idle | dnd | invisible
    public string Activity { get; set; } = "playing"; // playing | watching | listening | competing | custom | streaming
    public string ActivityText { get; set; } = "";
    public string StreamUrl { get; set; } = "";

    // Live state (not persisted)
    [JsonIgnore] public DiscordSocketClient? Client { get; set; }
    [JsonIgnore] public bool Running { get; set; }
    [JsonIgnore] public string LiveState { get; set; } = "offline"; // connecting | online | offline | reconnecting
    [JsonIgnore] public int GuildCount { get; set; }
    [JsonIgnore] public DateTime StartedAt { get; set; }
    [JsonIgnore] public int UptimeSecs { get; set; }
    [JsonIgnore] public int RestartCount { get; set; }
    [JsonIgnore] public bool ReportedOnline { get; set; }

    // Display helpers for the UI
    [JsonIgnore] public ImageSource? AvatarImage { get; set; }
    [JsonIgnore] public string StateText => LiveState == "online" ? "Online"
        : LiveState == "connecting" ? "Connecting…"
        : LiveState == "reconnecting" ? "Reconnecting…"
        : "Offline";
    [JsonIgnore] public Brush StateBrush => LiveState switch
    {
        "online" => new SolidColorBrush(MediaColor.FromRgb(0x57, 0xF2, 0x87)),
        "connecting" => new SolidColorBrush(MediaColor.FromRgb(0xFE, 0xBB, 0x3D)),
        "reconnecting" => new SolidColorBrush(MediaColor.FromRgb(0xFE, 0xBB, 0x3D)),
        _ => new SolidColorBrush(MediaColor.FromRgb(0x71, 0x71, 0x7A)),
    };
    [JsonIgnore] public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    [JsonIgnore] public string GuildCountText => GuildCount == 0 ? "—" : $"{GuildCount}";
}

/// <summary>
/// Owns every bot's DiscordSocketClient. Discord.Net auto-reconnects, so a
/// dropped connection recovers on its own; AutoRestart additionally forces a
/// fresh client when a gateway session ends for good.
/// </summary>
public sealed class BotManager
{
    public ObservableCollection<BotEntry> Bots { get; } = new();
    private readonly object _lock = new();

    /// <summary>entry, formatted console line, severity.</summary>
    public event Action<BotEntry, string, LogSeverity>? LogLine;
    public event Action<BotEntry>? StateChanged;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string StorePath => System.IO.Path.Combine(AppPaths.LocalDataDir, "bot_hoster_bots.json");

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

    /// <summary>Validates a token against the API and returns bot info, or null.</summary>
    public static async Task<(string Name, string Avatar, ulong Id)?> FetchBotInfoAsync(string token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // Discord requires the "Bot" scheme for bot tokens - "Bearer" is
            // only accepted for OAuth2 user tokens and returns 401 here.
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token.Trim());
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CFG2-BotHoster/1.0");
            var resp = await client.GetAsync("https://discord.com/api/v10/users/@me");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var name = root.GetProperty("username").GetString() ?? "Unnamed bot";
            // Discord snowflake IDs exceed JS safe integers and arrive as JSON
            // strings - GetUInt64() would throw on them.
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

    public bool CanAdd() => LicenseService.IsPremiumActive || Bots.Count == 0;

    public async Task<BotEntry?> AddBotAsync(string token, bool autoStart)
    {
        var info = await FetchBotInfoAsync(token);
        if (info == null) return null;

        // Apply the user's defaults for new bots.
        var s = AppSettings.Load();
        var entry = new BotEntry
        {
            Token = token.Trim(),
            Name = info.Value.Name,
            AvatarUrl = info.Value.Avatar,
            Id = info.Value.Id,
            AutoStart = autoStart,
            AutoRestart = s.AutoRestartNew,
            Status = s.DefaultStatus,
            Activity = s.DefaultActivity,
            ActivityText = s.DefaultActivityText,
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

    public async Task StartAsync(BotEntry entry)
    {
        lock (_lock)
        {
            if (entry.Running) return;
            entry.Running = true;
            entry.RestartCount = 0;
        }

        try
        {
            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds,
                LogLevel = LogSeverity.Verbose,
                AlwaysDownloadUsers = false,
                MessageCacheSize = 0,
            };
            var client = new DiscordSocketClient(config);
            entry.Client = client;

            client.Log += msg => OnLog(entry, msg);
            client.Ready += () =>
            {
                entry.LiveState = "online";
                entry.StartedAt = DateTime.Now;
                entry.GuildCount = client.Guilds.Count;
                entry.RestartCount = 0;
                Log(entry, $"Connected as {client.CurrentUser?.Username}#{client.CurrentUser?.DiscriminatorValue} " +
                           $"(in {entry.GuildCount} servers)", LogSeverity.Info);
                _ = ApplyPresenceAsync(entry);
                StateChanged?.Invoke(entry);
                if (!entry.ReportedOnline)
                {
                    entry.ReportedOnline = true;
                    _ = TelemetryService.ReportBotAsync(entry.Name, entry.Id, "online");
                }
                return Task.CompletedTask;
            };
            client.Disconnected += ex =>
            {
                if (entry.Running)
                {
                    entry.LiveState = "reconnecting";
                    Log(entry, $"Disconnected: {ex?.Message ?? "connection lost"} - Discord.Net is reconnecting",
                        LogSeverity.Warning);
                    StateChanged?.Invoke(entry);
                    if (entry.AutoRestart) _ = HardRestartIfStuckAsync(entry, ex);
                }
                return Task.CompletedTask;
            };
            client.GuildAvailable += _ =>
            {
                entry.GuildCount = client.Guilds.Count;
                StateChanged?.Invoke(entry);
                return Task.CompletedTask;
            };
            client.LoggedOut += () =>
            {
                entry.LiveState = "offline";
                entry.Running = false;
                Log(entry, "Logged out", LogSeverity.Info);
                StateChanged?.Invoke(entry);
                return Task.CompletedTask;
            };

            Log(entry, "Connecting to Discord gateway...", LogSeverity.Info);
            entry.LiveState = "connecting";
            StateChanged?.Invoke(entry);
            await client.LoginAsync(TokenType.Bot, entry.Token);
            await client.StartAsync();
        }
        catch (Exception ex)
        {
            lock (_lock) entry.Running = false;
            entry.LiveState = "offline";
            Log(entry, $"Failed to start: {ex.Message}", LogSeverity.Error);
            StateChanged?.Invoke(entry);
        }
    }

    /// <summary>
    /// When a gateway session ends permanently (not a normal reconnect),
    /// spins up a fresh client after a short wait so the bot comes back.
    /// </summary>
    private async Task HardRestartIfStuckAsync(BotEntry entry, Exception? reason)
    {
        try
        {
            await Task.Delay(8000);
            lock (_lock)
            {
                if (!entry.Running) return;
            }
            if (entry.Client is { ConnectionState: ConnectionState.Connected }) return;

            Log(entry, "Session ended - forcing a fresh connection...", LogSeverity.Warning);
            await StopAsync(entry);
            entry.RestartCount++;
            await StartAsync(entry);
        }
        catch { }
    }

    public async Task StopAsync(BotEntry entry)
    {
        lock (_lock) entry.Running = false;
        var client = entry.Client;
        entry.Client = null;
        if (client != null)
        {
            try
            {
                await client.StopAsync();
                await client.LogoutAsync();
            }
            catch { }
            try { client.Dispose(); } catch { }
        }
        entry.LiveState = "offline";
        Log(entry, "Stopped", LogSeverity.Info);
        StateChanged?.Invoke(entry);
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

    /// <summary>Applies the entry's configured presence to a live client.</summary>
    public async Task ApplyPresenceAsync(BotEntry entry)
    {
        var client = entry.Client;
        if (client?.ConnectionState != ConnectionState.Connected) return;
        try
        {
            var status = entry.Status switch
            {
                "idle" => UserStatus.Idle,
                "dnd" => UserStatus.DoNotDisturb,
                "invisible" => UserStatus.Invisible,
                _ => UserStatus.Online,
            };
            await client.SetStatusAsync(status);

            var text = entry.ActivityText ?? "";
            switch (entry.Activity)
            {
                case "watching":
                    await client.SetGameAsync(text, type: ActivityType.Watching);
                    break;
                case "listening":
                    await client.SetGameAsync(text, type: ActivityType.Listening);
                    break;
                case "competing":
                    await client.SetGameAsync(text, type: ActivityType.Competing);
                    break;
                case "streaming":
                    await client.SetGameAsync(text,
                        string.IsNullOrWhiteSpace(entry.StreamUrl) ? null : entry.StreamUrl,
                        ActivityType.Streaming);
                    break;
                case "custom":
                    await client.SetCustomStatusAsync(text);
                    break;
                default:
                    await client.SetGameAsync(text, type: ActivityType.Playing);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log(entry, $"Presence update failed: {ex.Message}", LogSeverity.Warning);
        }
    }

    private Task OnLog(BotEntry entry, LogMessage msg)
    {
        var text = string.IsNullOrWhiteSpace(msg.Message)
            ? msg.Exception?.Message ?? ""
            : msg.Message;
        if (string.IsNullOrWhiteSpace(text) && msg.Exception == null) return Task.CompletedTask;
        if (msg.Severity == LogSeverity.Debug) return Task.CompletedTask; // too noisy
        Log(entry, $"[{msg.Source}] {text}", msg.Severity);
        return Task.CompletedTask;
    }

    public void Log(BotEntry entry, string text, LogSeverity severity = LogSeverity.Info)
    {
        LogLine?.Invoke(entry, text, severity);
    }

    public void ClearLog(BotEntry entry)
    {
        LogCleared?.Invoke(entry);
    }

    public event Action<BotEntry>? LogCleared;
}
