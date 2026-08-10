using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Configuration2App.Models;

/// <summary>
/// One embed field row. Raises PropertyChanged so the live preview can refresh as you type.
/// </summary>
public class EmbedField : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _value = string.Empty;
    private bool _inline;

    public string Name
    {
        get => _name;
        set { _name = value; Notify(); }
    }

    public string Value
    {
        get => _value;
        set { _value = value; Notify(); }
    }

    public bool IsInline
    {
        get => _inline;
        set { _inline = value; Notify(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The embed currently being composed in the editor.</summary>
public class EmbedDraft
{
    public string Content { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "5865F2";
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorUrl { get; set; } = string.Empty;
    public string AuthorIcon { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string FooterText { get; set; } = string.Empty;
    public string FooterIcon { get; set; } = string.Empty;
    public bool IncludeTimestamp { get; set; } = true;
    public List<EmbedField> Fields { get; set; } = new();

    /// <summary>Parses "#RRGGBB" or "RRGGBB" into Discord's decimal color, or null when invalid.</summary>
    public static int? ParseColor(string hex)
    {
        var clean = hex.Trim().TrimStart('#');
        if (clean.Length != 6) return null;
        if (!int.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return null;
        return value;
    }
}

/// <summary>A full webhook message: plain content plus up to ten embeds.</summary>
public class MessageDraft
{
    public string Content { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public bool Tts { get; set; }
    public List<EmbedDraft> Embeds { get; set; } = new();
}
