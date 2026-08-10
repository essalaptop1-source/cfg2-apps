namespace Configuration2App.Models;

public class ConfigItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    [Newtonsoft.Json.JsonProperty("Url")]
    public string Url { get; set; } = string.Empty;

    [Newtonsoft.Json.JsonProperty("ContentUrl")]
    public string ContentUrl { get; set; } = string.Empty;

    public string Category { get; set; } = "General";
    public List<string> Tags { get; set; } = new();
    public bool IsPublic { get; set; }
    public bool IsDownloaded { get; set; }
    public bool IsFavorite { get; set; }
    public bool HasUpdate { get; set; }
    public string? LocalVersion { get; set; }
    public string? LocalPath { get; set; }
    public DateTime? DownloadedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }
    public string Version { get; set; } = "1.0";
    public int Downloads { get; set; }

    public string AuthorInitials
    {
        get
        {
            var parts = Author.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
            return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
        }
    }

    public string CategoryInitial => string.IsNullOrEmpty(Category) ? "G" : Category[..1].ToUpperInvariant();
}
