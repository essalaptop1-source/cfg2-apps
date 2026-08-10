namespace Configuration2App.Models;

public class AppSettings
{
    public string WebhookUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string EmbedContent { get; set; } = string.Empty;
    public bool Tts { get; set; }
    public string Theme { get; set; } = "indigo";
    public string FontName { get; set; } = "Segoe UI";
    public string FontSizePreset { get; set; } = "M";
    public List<EmbedDraft> Embeds { get; set; } = new() { new EmbedDraft() };

    // Auto-update: either a GitHub repo (owner/repo) or a version.json URL.
    public string GitHubRepo { get; set; } = string.Empty;
    public string UpdateUrl { get; set; } = string.Empty;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string SkippedVersion { get; set; } = string.Empty;
}
