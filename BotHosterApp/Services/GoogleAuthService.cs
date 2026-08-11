using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BotHosterApp.Services;

/// <summary>The Google identity of the signed-in user.</summary>
public sealed class GoogleProfile
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Picture { get; set; } = "";
    public DateTime SignedInAt { get; set; }
}

/// <summary>
/// Google sign-in via the standard OAuth 2.0 "loopback" flow (Google's
/// recommended flow for desktop apps):
///   1. Open the consent page in the default browser.
///   2. Catch the redirect on http://localhost:{LoopbackPort}/.
///   3. Exchange the code for tokens, then read name/email/picture from the
///      id_token JWT (no refresh token needed - identity only).
/// The developer must create a Google Cloud OAuth client ID (Desktop app type)
/// and set it in settings; the redirect URI http://localhost:52621/ must be
/// added to that client's authorized redirect URIs.
/// </summary>
public static class GoogleAuthService
{
    private const int LoopbackPort = 52621;
    private static string SessionPath => Path.Combine(AppPaths.LocalDataDir, "bot_hoster_google.json");

    // ============ DEVELOPER: one-time Google Cloud setup (takes ~2 minutes) ============
    // 1. Go to https://console.cloud.google.com/apis/credentials
    // 2. Create credentials -> OAuth client ID -> type "Desktop app".
    // 3. Under "Authorized redirect URIs" add:  http://localhost:52621/
    // 4. Paste the Client ID (and the Client Secret, if Google shows one) below.
    //
    // This is the ONLY thing that needs configuring - it is compiled into the
    // app, so end users just click "Sign in with Google" and it works.
    // No settings screen is needed.
    public const string ClientId = "";
    public const string ClientSecret = "";

    /// <summary>True once the developer has pasted a real Client ID.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public static GoogleProfile? Current { get; private set; }

    public static bool IsSignedIn => Current != null;

    public static void LoadSession()
    {
        try
        {
            if (File.Exists(SessionPath))
                Current = JsonSerializer.Deserialize<GoogleProfile>(File.ReadAllText(SessionPath));
        }
        catch { Current = null; }
    }

    private static void SaveSession(GoogleProfile? p)
    {
        try
        {
            if (p == null)
            {
                if (File.Exists(SessionPath)) File.Delete(SessionPath);
            }
            else
            {
                File.WriteAllText(SessionPath, JsonSerializer.Serialize(p));
            }
        }
        catch { }
    }

    public static void SignOut()
    {
        Current = null;
        SaveSession(null);
    }

    /// <summary>Runs the loopback sign-in. Returns (ok, message, profile).</summary>
    public static async Task<(bool Ok, string Msg, GoogleProfile? Profile)> SignInAsync(string clientId, string clientSecret)
    {
        return await SignInCoreAsync(clientId, clientSecret);
    }

    /// <summary>Runs the loopback sign-in using the compiled-in credentials.</summary>
    public static async Task<(bool Ok, string Msg, GoogleProfile? Profile)> SignInAsync()
    {
        return await SignInCoreAsync(ClientId, ClientSecret);
    }

    private static async Task<(bool Ok, string Msg, GoogleProfile? Profile)> SignInCoreAsync(string clientId, string clientSecret)
    {
        var redirect = $"http://localhost:{LoopbackPort}/";
        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(redirect);
            listener.Start();
        }
        catch
        {
            return (false, $"Could not open the local callback port {LoopbackPort}. Close anything using it and try again.", null);
        }

        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth" +
                      $"?client_id={Uri.EscapeDataString(clientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
                      "&response_type=code&scope=openid%20email%20profile&prompt=select_account";
        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch
        {
            return (false, "Could not open your browser to finish the sign-in.", null);
        }

        string? code;
        try
        {
            var ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(3));
            code = ctx.Request.QueryString["code"];
            var body = string.IsNullOrEmpty(code)
                ? "<html><body style='font-family:sans-serif;text-align:center;padding:48px'><h2>Sign-in failed</h2><p>You can close this tab and go back to the app.</p></body></html>"
                : "<html><body style='font-family:sans-serif;text-align:center;padding:48px'><h2>Signed in!</h2><p>You can close this tab and go back to the app.</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "text/html";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch
        {
            return (false, "Sign-in timed out - try again.", null);
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        if (string.IsNullOrEmpty(code))
            return (false, "Google sign-in was cancelled.", null);

        // Exchange the authorization code for tokens.
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var form = new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = clientId,
                ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code",
            };
            if (!string.IsNullOrWhiteSpace(clientSecret)) form["client_secret"] = clientSecret;

            var resp = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
            if (!resp.IsSuccessStatusCode)
                return (false, "Google rejected the sign-in - check the Client ID / Secret in the developer settings.", null);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var idToken = doc.RootElement.GetProperty("id_token").GetString();
            if (string.IsNullOrEmpty(idToken))
                return (false, "No identity token was returned.", null);

            var payload = DecodeJwtPayload(idToken);
            if (payload == null)
                return (false, "Could not decode your Google profile.", null);
            var p = payload.Value;

            var profile = new GoogleProfile
            {
                Name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Email = p.TryGetProperty("email", out var em) ? em.GetString() ?? "" : "",
                Picture = p.TryGetProperty("picture", out var pic) ? pic.GetString() ?? "" : "",
                SignedInAt = DateTime.UtcNow,
            };
            Current = profile;
            SaveSession(profile);
            return (true, $"Signed in as {profile.Name}.", profile);
        }
        catch
        {
            return (false, "Network error while finishing the sign-in.", null);
        }
    }

    private static JsonElement? DecodeJwtPayload(string idToken)
    {
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string Base64UrlDecode(string s)
    {
        var pad = s.Length % 4;
        if (pad > 0) s += new string('=', 4 - pad);
        return Encoding.UTF8.GetString(Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/')));
    }
}
