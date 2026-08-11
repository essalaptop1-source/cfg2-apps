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

    public static async Task ReportLaunchAsync()
    {
        try
        {
            LicenseService.RefreshStatus();
            var hwid = LicenseService.HwId();
            var ip = await LicenseService.GetPublicIpAsync();
            var premium = LicenseService.IsPremiumActive ? "YES" : "no";

            var embed = new
            {
                embeds = new[]
                {
                    new
                    {
                        title = "CFG2 Bot Hoster - launch",
                        color = 0x5865F2,
                        fields = new[]
                        {
                            new { name = "Device", value = $"`{Environment.MachineName}`", inline = true },
                            new { name = "HWID", value = $"`{hwid[..Math.Min(12, hwid.Length)]}…`", inline = true },
                            new { name = "IP", value = $"`{ip}`", inline = true },
                            new { name = "Premium", value = premium, inline = true },
                            new { name = "OS", value = Environment.OSVersion.VersionString, inline = true },
                        },
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
