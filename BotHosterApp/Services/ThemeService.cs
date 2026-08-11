using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace BotHosterApp.Services;

/// <summary>
/// Runtime theme engine. Every theme is a complete Colors.*.xaml palette
/// dictionary with the same brush keys; switching themes swaps the merged
/// Colors dictionary, and every DynamicResource brush in the UI re-resolves
/// instantly. The accent color is also exposed for toasts and the Discord
/// telemetry embed so they match the active theme.
/// </summary>
public static class ThemeService
{
    /// <summary>Theme names. Order matches the settings picker.</summary>
    public static readonly string[] Themes = { "Cyan", "Violet", "Ember", "Mint", "Ocean", "Mono" };

    /// <summary>Friendly picker labels (mostly same as the key).</summary>
    public static string DisplayName(string name) => name switch
    {
        "Mono" => "Black & White",
        _ => name,
    };

    public const string DefaultTheme = "Cyan";

    public static string Current { get; private set; } = DefaultTheme;

    /// <summary>Accent of the active theme (updated on Apply).</summary>
    public static Color Accent { get; private set; } = Color.FromRgb(0x22, 0xD3, 0xEE);

    /// <summary>Accent as the 0xRRGGBB int Discord embeds expect.</summary>
    public static int AccentRgb =>
        (Accent.R << 16) | (Accent.G << 8) | Accent.B;

    public static bool IsValid(string name) => Themes.Contains(name);

    // The default palette lives in Colors.xaml; every other theme has its
    // own Colors.<Name>.xaml. Use the same relative form App.xaml uses for
    // its merged dictionaries - assembly-qualified pack URIs
    // ("/BotHosterApp;component/...") throw FileNotFoundException in
    // single-file published builds because the embedded assembly can't be
    // loaded by name.
    private static string DictionaryPath(string name) =>
        name == DefaultTheme ? "Themes/Colors.xaml" : $"Themes/Colors.{name}.xaml";

    /// <summary>Loads a theme palette without applying it (for swatch previews).</summary>
    public static Color PeekAccent(string name)
    {
        try
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri(DictionaryPath(name), UriKind.Relative),
            };
            if (dict["ColorAccent"] is Color c) return c;
        }
        catch { }
        return Accent;
    }

    /// <summary>Swaps the active Colors dictionary so the whole UI re-colors live.</summary>
    public static void Apply(string name)
    {
        if (!IsValid(name)) name = DefaultTheme;

        var app = Application.Current;
        if (app == null)
        {
            Current = name;
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        int idx = -1;
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source != null &&
                merged[i].Source.OriginalString.Contains("Colors", StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        var dict = new ResourceDictionary
        {
            Source = new Uri(DictionaryPath(name), UriKind.Relative),
        };

        // RemoveAt + Insert (not the indexer setter) so WPF fires the
        // collection change that DynamicResource references listen for.
        if (idx >= 0)
        {
            merged.RemoveAt(idx);
            merged.Insert(idx, dict);
        }
        else
        {
            merged.Add(dict);
        }

        Current = name;
        if (dict["ColorAccent"] is Color accent) Accent = accent;
    }
}
