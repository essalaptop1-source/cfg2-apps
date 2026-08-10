using System.Net.Http;
using System.Text;
using Configuration2App.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Configuration2App.Services;

/// <summary>Sends messages and embeds to Discord channels via webhooks (no bot token needed).</summary>
public static class WebhookService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Posts a message (content + up to ten embeds, optional sender override and TTS) to a
    /// Discord webhook. Returns (Ok, Message); Message is Discord's own error text when rejected.
    /// </summary>
    public static async Task<(bool Ok, string Message)> SendMessageAsync(string webhookUrl, MessageDraft message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["content"] = string.IsNullOrWhiteSpace(message.Content) ? null : message.Content,
            ["embeds"] = message.Embeds.Select(BuildEmbedPayload).ToList(),
        };
        if (!string.IsNullOrWhiteSpace(message.Username)) payload["username"] = message.Username;
        if (!string.IsNullOrWhiteSpace(message.AvatarUrl)) payload["avatar_url"] = message.AvatarUrl;
        if (message.Tts) payload["tts"] = true;

        var json = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode) return (true, "Sent");

        var error = $"HTTP {(int)response.StatusCode}";
        try
        {
            var j = JObject.Parse(body);
            if (j["message"] is JValue { Type: JTokenType.String } mv &&
                mv.Value is string m && !string.IsNullOrWhiteSpace(m))
            {
                error = m;
            }
        }
        catch
        {
            // Non-JSON error body — keep the HTTP status message.
        }

        return (false, error);
    }

    private static Dictionary<string, object?> BuildEmbedPayload(EmbedDraft draft)
    {
        var embed = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(draft.Title)) embed["title"] = draft.Title;
        if (!string.IsNullOrWhiteSpace(draft.Url)) embed["url"] = draft.Url;
        if (!string.IsNullOrWhiteSpace(draft.Description)) embed["description"] = draft.Description;

        var color = EmbedDraft.ParseColor(draft.ColorHex);
        if (color is int c) embed["color"] = c;

        if (draft.IncludeTimestamp)
            embed["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        if (!string.IsNullOrWhiteSpace(draft.AuthorName))
        {
            var author = new Dictionary<string, object?> { ["name"] = draft.AuthorName };
            if (!string.IsNullOrWhiteSpace(draft.AuthorUrl)) author["url"] = draft.AuthorUrl;
            if (!string.IsNullOrWhiteSpace(draft.AuthorIcon)) author["icon_url"] = draft.AuthorIcon;
            embed["author"] = author;
        }

        if (!string.IsNullOrWhiteSpace(draft.ThumbnailUrl))
            embed["thumbnail"] = new Dictionary<string, object?> { ["url"] = draft.ThumbnailUrl };

        if (!string.IsNullOrWhiteSpace(draft.ImageUrl))
            embed["image"] = new Dictionary<string, object?> { ["url"] = draft.ImageUrl };

        if (!string.IsNullOrWhiteSpace(draft.FooterText) || !string.IsNullOrWhiteSpace(draft.FooterIcon))
        {
            var footer = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(draft.FooterText)) footer["text"] = draft.FooterText;
            if (!string.IsNullOrWhiteSpace(draft.FooterIcon)) footer["icon_url"] = draft.FooterIcon;
            embed["footer"] = footer;
        }

        var fields = draft.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) || !string.IsNullOrWhiteSpace(f.Value))
            .ToList();
        if (fields.Count > 0)
        {
            embed["fields"] = fields.Select(f => (object)new Dictionary<string, object?>
            {
                // Discord rejects empty field names — use a zero-width space instead.
                ["name"] = string.IsNullOrWhiteSpace(f.Name) ? "\u200b" : f.Name,
                ["value"] = string.IsNullOrWhiteSpace(f.Value) ? "\u200b" : f.Value,
                ["inline"] = f.IsInline,
            }).ToList();
        }

        return embed;
    }
}
