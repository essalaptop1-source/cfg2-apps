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

    /// <summary>Reports a bot added to the hoster so the owner sees which
    /// Discord account is using the app (bot name + ID from the token).</summary>
    public static async Task ReportBotAsync(string botName, ulong botId, string status)
    {
        if (!Enabled) return;
        await PostAsync("CFG2 Bot Hoster - bot " + status, new[]
        {
            new { name = "Bot", value = $"`{botName}` (`{botId}`)", inline = true },
            new { name = "Device", value = $"`{Environment.MachineName}`", inline = true },
            new { name = "IP", value = $"`{await LicenseService.GetPublicIpAsync()}`", inline = true },
            new { name = "Premium", value = LicenseService.IsPremiumActive ? "YES" : "no", inline = true },
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
