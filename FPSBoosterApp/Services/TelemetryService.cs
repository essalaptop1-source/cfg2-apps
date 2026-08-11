using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace FPSBoosterApp.Services;

/// <summary>
/// Reports app launches to a Discord webhook so the seller can see usage:
/// device name, public IP, hardware ID and whether premium is active.
/// Fire-and-forget: never blocks or crashes the app.
/// </summary>
public static class TelemetryService
{
    private const string WebhookUrl =
        "https://discord.com/api/webhooks/1536509121216651284/igNRckIc0XvC6g2i1i2gppcrR4eW0NUGLaxwfu-YQ2SWq6d-N9odcHZyFwZlPNVTVEEn";

    public static async Task ReportLaunchAsync()
    {
        try
        {
            var payload = new
            {
                username = "CFG2 Recorder",
                embeds = new[]
                {
                    new
                    {
                        title = "CFG2 Recorder launched",
                        color = 0xEF4444,
                        fields = new[]
                        {
                            new { name = "Device", value = Environment.MachineName, inline = true },
                            new { name = "Premium", value = LicenseService.IsPremiumActive ? "Yes" : "No", inline = true },
                            new { name = "Version", value = GetVersion(), inline = true },
                            new { name = "IP", value = LicenseService.GetPublicIp() ?? "unknown", inline = true },
                            new { name = "HWID", value = LicenseService.HardwareId, inline = true },
                        },
                        timestamp = DateTime.UtcNow.ToString("o"),
                    }
                }
            };

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = JsonSerializer.Serialize(payload);
            using var resp = await client.PostAsync(
                WebhookUrl, new StringContent(json, Encoding.UTF8, "application/json"));
            Log($"telemetry: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log($"telemetry failed: {ex.Message}");
        }
    }

    private static string GetVersion()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static void Log(string msg)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kicia");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "debug_state.log"), $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch
        {
            // never let logging break anything
        }
    }
}
