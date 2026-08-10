using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Configuration2App.Services;

public static class ThemeService
{
    public static readonly string[] FontChoices =
    {
        "Segoe UI", "Calibri", "Candara", "Verdana", "Trebuchet MS", "Consolas", "Georgia",
    };

    public static readonly string[] ThemeKeys =
    {
        "indigo", "violet", "green", "red", "orange", "cyan", "pink",
    };

    private sealed record Palette(string Accent, string Text, string GradStart, string GradEnd);

    private static readonly Dictionary<string, Palette> Palettes = new()
    {
        ["indigo"] = new("#6366F1", "#A5B4FC", "#6366F1", "#8B5CF6"),
        ["violet"] = new("#8B5CF6", "#C4B5FD", "#8B5CF6", "#A855F7"),
        ["green"]  = new("#10B981", "#6EE7B7", "#10B981", "#34D399"),
        ["red"]    = new("#EF4444", "#FCA5A5", "#EF4444", "#F97373"),
        ["orange"] = new("#F97316", "#FDBA74", "#F97316", "#FBBF24"),
        ["cyan"]   = new("#06B6D4", "#67E8F9", "#06B6D4", "#22D3EE"),
        ["pink"]   = new("#EC4899", "#F9A8D4", "#EC4899", "#F472B6"),
    };

    private static readonly Color White = Colors.White;
    private static readonly Color Black = Colors.Black;

    public static double ScaleForPreset(string preset) => preset switch
    {
        "S" => 0.9,
        "L" => 1.15,
        _ => 1.0,
    };

    /// <summary>
    /// Mutates the shared accent brushes in Application.Resources in place, so every
    /// StaticResource consumer across the app repaints live.
    /// </summary>
    public static bool ApplyAccent(string key)
    {
        if (!Palettes.TryGetValue(key, out var p)) return false;
        var res = Application.Current.Resources;
        var accent = Parse(p.Accent);
        var text = Parse(p.Text);
        var gs = Parse(p.GradStart);
        var ge = Parse(p.GradEnd);

        // Replace, don't mutate: WPF freezes shared resource brushes, so live
        // theming works by installing fresh (unfrozen) instances under the same
        // keys — DynamicResource consumers repaint automatically.
        Put(res, "AccentBrush", new SolidColorBrush(accent));
        Put(res, "AccentHoverBrush", new SolidColorBrush(Blend(accent, White, 0.08)));
        Put(res, "AccentPressedBrush", new SolidColorBrush(Blend(accent, Black, 0.14)));
        Put(res, "AccentTextBrush", new SolidColorBrush(text));
        Put(res, "AccentSoftBrush", new SolidColorBrush(Alpha(accent, 0x1A)));
        Put(res, "AccentBorderBrush", new SolidColorBrush(Alpha(accent, 0x3D)));
        PutGradient(res, "AccentGradientBrush", gs, ge);
        PutGradient(res, "AccentGradientHoverBrush", Blend(gs, White, 0.08), Blend(ge, White, 0.08));
        PutGradient(res, "AccentGradientPressedBrush", Blend(gs, Black, 0.14), Blend(ge, Black, 0.14));

        // Surfaces — a hue-tinted dark palette derived from the accent, so the
        // whole UI (backgrounds, panels, cards, borders) follows the theme.
        var hue = ToHsl(accent).h;
        Put(res, "BgBrush", new SolidColorBrush(Hsl(hue, 0.16, 0.045)));
        Put(res, "BgElevatedBrush", new SolidColorBrush(Hsl(hue, 0.15, 0.065)));
        Put(res, "SurfaceBrush", new SolidColorBrush(Hsl(hue, 0.13, 0.09)));
        Put(res, "SurfaceAltBrush", new SolidColorBrush(Hsl(hue, 0.13, 0.145)));
        Put(res, "SurfaceHoverBrush", new SolidColorBrush(Hsl(hue, 0.13, 0.145)));
        Put(res, "SurfaceActiveBrush", new SolidColorBrush(Hsl(hue, 0.13, 0.185)));
        Put(res, "CardBrush", new SolidColorBrush(Hsl(hue, 0.14, 0.055)));
        Put(res, "BorderBrush", new SolidColorBrush(Hsl(hue, 0.12, 0.15)));
        Put(res, "BorderStrongBrush", new SolidColorBrush(Hsl(hue, 0.12, 0.24)));
        Put(res, "DividerBrush", new SolidColorBrush(Hsl(hue, 0.12, 0.10)));
        Put(res, "PanelDividerBrush", new SolidColorBrush(Hsl(hue, 0.12, 0.10)));
        Put(res, "SidebarActiveBrush", new SolidColorBrush(Hsl(hue, 0.13, 0.09)));
        Put(res, "WindowEdgeBrush", new SolidColorBrush(Hsl(hue, 0.12, 0.24)));
        return true;
    }

    private static void Put(ResourceDictionary res, string key, SolidColorBrush brush)
    {
        res[key] = brush;
    }

    private static void PutGradient(ResourceDictionary res, string key, Color start, Color end)
    {
        res[key] = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(start, 0),
                new GradientStop(end, 1),
            },
        };
    }



    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Blend(Color c, Color other, double t) => Color.FromArgb(
        c.A,
        (byte)(c.R + (other.R - c.R) * t),
        (byte)(c.G + (other.G - c.G) * t),
        (byte)(c.B + (other.B - c.B) * t));

    private static (double h, double s, double l) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    private static Color Hsl(double h, double s, double l)
    {
        if (s == 0)
        {
            var gray = (byte)Math.Round(l * 255);
            return Color.FromRgb(gray, gray, gray);
        }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double r = Hue2Rgb(p, q, h + 1.0 / 3);
        double g = Hue2Rgb(p, q, h);
        double b = Hue2Rgb(p, q, h - 1.0 / 3);
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double Hue2Rgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    /// <summary>
    /// Applies the chosen font family and size scale to every text-bearing element in
    /// the tree (local values, so they override style-set fonts). Original sizes are
    /// captured on first application so switching scales stays consistent.
    /// </summary>
    public static void ApplyFontToTree(DependencyObject node, string family, double scale,
        Dictionary<DependencyObject, double> originals)
    {
        if (node is Control or TextBlock)
            ApplyTo((FrameworkElement)node, family, scale, originals, node);

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            ApplyFontToTree(VisualTreeHelper.GetChild(node, i), family, scale, originals);
    }

    private static void ApplyTo(FrameworkElement element, string family, double scale,
        Dictionary<DependencyObject, double> originals, DependencyObject key)
    {
        TextElement.SetFontFamily(element, new FontFamily(family));
        if (!originals.TryGetValue(key, out var original))
        {
            original = TextElement.GetFontSize(element);
            originals[key] = original;
        }
        TextElement.SetFontSize(element, original * scale);
    }
}
