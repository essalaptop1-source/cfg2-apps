using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BotHosterApp.Services;

public sealed record AdminGuild(ulong Id, string Name, int Members);
public sealed record AdminChannel(ulong Id, string Name, int Type, int Position, ulong? ParentId, string OverwritesJson);
public sealed record AdminMember(ulong Id, string Username, string Nick, bool IsBot, string RoleIdsJson);
public sealed record AdminRole(ulong Id, string Name, ulong Permissions, int Position, uint Color, bool Managed);

/// <summary>
/// Discord REST management for the admin panel: servers, channels, members,
/// roles and the bot's effective permissions (computed from role + overwrite
/// resolution since Discord exposes no endpoint that returns them directly).
/// </summary>
public static class AdminService
{
    // ---------------------------------------------------------------- permission flags
    public static readonly (ulong Bit, string Name)[] PermFlags =
    {
        (1UL << 0,  "Create instant invite"),
        (1UL << 1,  "Kick members"),
        (1UL << 2,  "Ban members"),
        (1UL << 3,  "Administrator"),
        (1UL << 4,  "Manage channels"),
        (1UL << 5,  "Manage server"),
        (1UL << 6,  "Add reactions"),
        (1UL << 7,  "View audit log"),
        (1UL << 8,  "Priority speaker"),
        (1UL << 9,  "Stream"),
        (1UL << 10, "View channel"),
        (1UL << 11, "Send messages"),
        (1UL << 12, "Send TTS messages"),
        (1UL << 13, "Manage messages"),
        (1UL << 14, "Embed links"),
        (1UL << 15, "Attach files"),
        (1UL << 16, "Read message history"),
        (1UL << 17, "Mention everyone"),
        (1UL << 18, "Use external emojis"),
        (1UL << 19, "View server insights"),
        (1UL << 20, "Connect"),
        (1UL << 21, "Speak"),
        (1UL << 22, "Mute members"),
        (1UL << 23, "Deafen members"),
        (1UL << 24, "Move members"),
        (1UL << 25, "Use voice activity"),
        (1UL << 26, "Change nickname"),
        (1UL << 27, "Manage nicknames"),
        (1UL << 28, "Manage roles"),
        (1UL << 29, "Manage webhooks"),
        (1UL << 30, "Manage expressions"),
        (1UL << 31, "Use slash commands"),
        (1UL << 32, "Request to speak"),
        (1UL << 33, "Manage events"),
        (1UL << 34, "Manage threads"),
        (1UL << 35, "Create public threads"),
        (1UL << 36, "Create private threads"),
        (1UL << 37, "Use external stickers"),
        (1UL << 38, "Send messages in threads"),
        (1UL << 39, "Use embedded activities"),
        (1UL << 40, "Moderate members"),
        (1UL << 41, "View creator analytics"),
        (1UL << 42, "Use soundboard"),
        (1UL << 46, "Send voice messages"),
    };

    public static List<string> DecodePermissions(ulong perms)
    {
        var result = new List<string>();
        if ((perms & (1UL << 3)) != 0) // Administrator
            return new List<string> { "Administrator (all permissions)" };
        foreach (var (bit, name) in PermFlags)
            if ((perms & bit) != 0)
                result.Add(name);
        return result;
    }

    // ---------------------------------------------------------------- plumbing

    private static HttpClient NewClient(string token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token.Trim());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CFG2-BotHoster/1.0");
        return client;
    }

    private static async Task<(bool Ok, JsonElement? Json, string Error)> GetJsonAsync(HttpClient client, string path)
    {
        try
        {
            var resp = await client.GetAsync("https://discord.com/api/v10" + path);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                string msg = body;
                try
                {
                    using var d = JsonDocument.Parse(body);
                    if (d.RootElement.TryGetProperty("message", out var m)) msg = m.GetString() ?? body;
                }
                catch { }
                return (false, null, $"{(int)resp.StatusCode} {msg}");
            }
            using var doc = JsonDocument.Parse(body);
            return (true, doc.RootElement.Clone(), "");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static async Task<(bool Ok, string Error)> SendJsonAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        try
        {
            var req = new HttpRequestMessage(method, "https://discord.com/api/v10" + path);
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await client.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return (true, "");
            string msg = text;
            try
            {
                using var d = JsonDocument.Parse(text);
                if (d.RootElement.TryGetProperty("message", out var m)) msg = m.GetString() ?? text;
            }
            catch { }
            return (false, $"{(int)resp.StatusCode} {msg}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static ulong ToUlong(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetUInt64(),
            JsonValueKind.String => ulong.TryParse(el.GetString(), out var v) ? v : 0,
            _ => 0,
        };
    }

    // ---------------------------------------------------------------- reads

    public static async Task<List<AdminGuild>> GetGuildsAsync(string token)
    {
        var result = new List<AdminGuild>();
        using var client = NewClient(token);
        var (ok, json, _) = await GetJsonAsync(client, "/users/@me/guilds");
        if (!ok || json == null) return result;
        foreach (var g in json.Value.EnumerateArray())
        {
            result.Add(new AdminGuild(
                ulong.Parse(g.GetProperty("id").GetString() ?? "0"),
                g.GetProperty("name").GetString() ?? "Unknown",
                g.TryGetProperty("approximate_member_count", out var m) ? m.GetInt32() : 0));
        }
        // Fetch real member counts in parallel (the guilds list endpoint omits them).
        await Task.WhenAll(result.Select(async guild =>
        {
            try
            {
                using var c2 = NewClient(token);
                var (ok2, j2, _) = await GetJsonAsync(c2, $"/guilds/{guild.Id}?with_counts=true");
                if (ok2 && j2 != null && j2.Value.TryGetProperty("approximate_member_count", out var m))
                    result[result.FindIndex(x => x.Id == guild.Id)] =
                        guild with { Members = m.GetInt32() };
            }
            catch { }
        }));
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static async Task<(List<AdminChannel> Channels, List<AdminRole> Roles, List<ulong> BotRoleIds, ulong EveryonePerms)> LoadGuildAsync(string token, ulong guildId, ulong botId)
    {
        using var client = NewClient(token);
        var channels = new List<AdminChannel>();
        var roles = new List<AdminRole>();
        var botRoles = new List<ulong>();
        ulong everyonePerms = 0;

        var (okC, jc, _) = await GetJsonAsync(client, $"/guilds/{guildId}/channels");
        if (okC && jc != null)
        {
            foreach (var c in jc.Value.EnumerateArray())
            {
                channels.Add(new AdminChannel(
                    ulong.Parse(c.GetProperty("id").GetString() ?? "0"),
                    c.GetProperty("name").GetString() ?? "unnamed",
                    c.TryGetProperty("type", out var t) ? t.GetInt32() : 0,
                    c.TryGetProperty("position", out var p) ? p.GetInt32() : 0,
                    c.TryGetProperty("parent_id", out var pid) && pid.ValueKind == JsonValueKind.String ? ulong.Parse(pid.GetString()!) : null,
                    c.TryGetProperty("permission_overwrites", out var po) ? po.GetRawText() : "[]"));
            }
        }

        var (okR, jr, _) = await GetJsonAsync(client, $"/guilds/{guildId}/roles");
        if (okR && jr != null)
        {
            foreach (var r in jr.Value.EnumerateArray())
            {
                var id = ulong.Parse(r.GetProperty("id").GetString() ?? "0");
                var perms = ToUlong(r.GetProperty("permissions"));
                roles.Add(new AdminRole(
                    id,
                    r.GetProperty("name").GetString() ?? "unnamed",
                    perms,
                    r.TryGetProperty("position", out var p) ? p.GetInt32() : 0,
                    r.TryGetProperty("color", out var col) ? (uint)(col.GetInt32() & 0xFFFFFF) : 0,
                    r.TryGetProperty("managed", out var mg) && mg.GetBoolean()));
                if (id == guildId) everyonePerms = perms; // @everyone role id == guild id
            }
            roles.Sort((a, b) => b.Position.CompareTo(a.Position));
        }

        var (okM, jm, _) = await GetJsonAsync(client, $"/guilds/{guildId}/members/{botId}");
        if (okM && jm != null && jm.Value.TryGetProperty("roles", out var rl))
        {
            foreach (var r in rl.EnumerateArray())
                if (ulong.TryParse(r.GetString(), out var rid)) botRoles.Add(rid);
        }

        return (channels, roles, botRoles, everyonePerms);
    }

    public static async Task<List<AdminMember>> GetMembersAsync(string token, ulong guildId, int limit = 100)
    {
        var result = new List<AdminMember>();
        using var client = NewClient(token);
        var (ok, json, _) = await GetJsonAsync(client, $"/guilds/{guildId}/members?limit={limit}");
        if (!ok || json == null) return result;
        foreach (var m in json.Value.EnumerateArray())
        {
            var user = m.TryGetProperty("user", out var u) ? u : default;
            result.Add(new AdminMember(
                user.ValueKind == JsonValueKind.Object && user.TryGetProperty("id", out var uid) ? ulong.Parse(uid.GetString() ?? "0") : 0,
                user.ValueKind == JsonValueKind.Object && user.TryGetProperty("username", out var un) ? un.GetString() ?? "unknown" : "unknown",
                m.TryGetProperty("nick", out var nk) && nk.ValueKind == JsonValueKind.String ? nk.GetString() ?? "" : "",
                user.ValueKind == JsonValueKind.Object && user.TryGetProperty("bot", out var b) && b.GetBoolean(),
                m.TryGetProperty("roles", out var rr) ? rr.GetRawText() : "[]"));
        }
        return result;
    }

    /// <summary>The bot's effective permissions in a channel, resolved from the
    /// guild base + channel overwrites (everyone first, then the bot's roles
    /// highest first, then the bot member overwrite). The @everyone overwrite
    /// uses the guild id as its overwrite id.</summary>
    public static ulong ComputeChannelPermissions(ulong guildId, AdminChannel channel, ulong guildBase, List<AdminRole> roles, List<ulong> botRoleIds, ulong botId)
    {
        var perms = guildBase;
        var overwrites = new List<(ulong Id, ulong Allow, ulong Deny)>();
        try
        {
            using var doc = JsonDocument.Parse(channel.OverwritesJson);
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                overwrites.Add((
                    ulong.Parse(o.GetProperty("id").GetString() ?? "0"),
                    ToUlong(o.GetProperty("allow")),
                    ToUlong(o.GetProperty("deny"))));
            }
        }
        catch { }

        void Apply(ulong id)
        {
            foreach (var (oid, allow, deny) in overwrites)
            {
                if (oid != id) continue;
                perms &= ~deny;
                perms |= allow;
            }
        }

        Apply(guildId);                 // @everyone overwrite (id == guild id)
        foreach (var role in roles)     // the bot's roles, highest position first
        {
            if (!botRoleIds.Contains(role.Id)) continue;
            Apply(role.Id);
        }
        Apply(botId);                   // member-specific overwrite
        return perms;
    }

    // ---------------------------------------------------------------- actions

    public static async Task<(bool Ok, string Error)> CreateChannelAsync(string token, ulong guildId, string name, int type = 0)
    {
        using var client = NewClient(token);
        return await SendJsonAsync(client, HttpMethod.Post, $"/guilds/{guildId}/channels",
            new { name, type });
    }

    public static async Task<(bool Ok, string Error)> DeleteChannelAsync(string token, ulong channelId)
    {
        using var client = NewClient(token);
        return await SendJsonAsync(client, HttpMethod.Delete, $"/channels/{channelId}");
    }

    public static async Task<(bool Ok, string Error)> RenameChannelAsync(string token, ulong channelId, string name)
    {
        using var client = NewClient(token);
        return await SendJsonAsync(client, HttpMethod.Patch, $"/channels/{channelId}", new { name });
    }

    public static async Task<(bool Ok, string Error)> KickMemberAsync(string token, ulong guildId, ulong memberId)
    {
        using var client = NewClient(token);
        return await SendJsonAsync(client, HttpMethod.Delete, $"/guilds/{guildId}/members/{memberId}");
    }

    public static async Task<(bool Ok, string Error)> BanMemberAsync(string token, ulong guildId, ulong memberId, string reason = "")
    {
        using var client = NewClient(token);
        return await SendJsonAsync(client, HttpMethod.Put, $"/guilds/{guildId}/bans/{memberId}", new { reason });
    }

    public static async Task<(bool Ok, string Error)> TimeoutMemberAsync(string token, ulong guildId, ulong memberId, int minutes)
    {
        using var client = NewClient(token);
        var until = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, minutes)).ToString("o");
        return await SendJsonAsync(client, HttpMethod.Patch, $"/guilds/{guildId}/members/{memberId}", new { communication_disabled_until = until });
    }

    public static async Task<(bool Ok, string Error)> CreateRoleAsync(string token, ulong guildId, string name)
    {
        using var client = NewClient(token);
        return await SendJsonAsync(client, HttpMethod.Post, $"/guilds/{guildId}/roles", new { name });
    }

    public static async Task<(bool Ok, string Error)> DeleteRoleAsync(string token, ulong guildId, ulong roleId)
    {
        using var client = NewClient(token);
        return await SendJsonAsync(client, HttpMethod.Delete, $"/guilds/{guildId}/roles/{roleId}");
    }

    public static async Task<(bool Ok, string Error)> RenameRoleAsync(string token, ulong guildId, ulong roleId, string name)
    {
        using var client = NewClient(token);
        return await SendJsonAsync(client, HttpMethod.Patch, $"/guilds/{guildId}/roles/{roleId}", new { name });
    }

    public static async Task<(bool Ok, string Error)> UpdateRolePermissionsAsync(string token, ulong guildId, ulong roleId, ulong permissions)
    {
        using var client = NewClient(token);
        return await SendJsonAsync(client, HttpMethod.Patch, $"/guilds/{guildId}/roles/{roleId}", new { permissions = permissions.ToString() });
    }
}
