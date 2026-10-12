using System.Globalization;
using System.Text.RegularExpressions;

namespace CbBridge.Core.Application;

/// <summary>
/// appsettings.json → "Branding". Lets any company put its own name, logo and colour on the bridge
/// without code changes. Logo files go in wwwroot/branding/ (or use a full https URL).
/// </summary>
public sealed class BrandingOptions
{
    public string ProductName { get; set; } = "CB Test Bridge";
    public string Tagline { get; set; } = "Test management ⇄ test framework";
    /// <summary>Shown next to the product name and used as the logo's alt text, e.g. "Acme Automotive".</summary>
    public string CompanyName { get; set; } = "";
    /// <summary>e.g. "branding/logo.svg" (file in wwwroot/branding) or "https://…/logo.png". Empty = built-in mark.</summary>
    public string LogoUrl { get; set; } = "";
    /// <summary>Logo height in the top bar, in px (width follows the image).</summary>
    public int LogoHeight { get; set; } = 28;
    /// <summary>e.g. "branding/favicon.png". Empty = browser default.</summary>
    public string FaviconUrl { get; set; } = "";
    /// <summary>Main colour of buttons and highlights, "#RRGGBB". Must be dark enough for white text.</summary>
    public string AccentColor { get; set; } = "#0E5E5A";
    /// <summary>Top bar background, "#RRGGBB". Must be dark enough for white text.</summary>
    public string HeaderColor { get; set; } = "#15181C";
}

/// <summary>Safe colour handling for branding (no CSS injection, readable text).</summary>
public static partial class BrandingTheme
{
    public const string DefaultAccent = "#0E5E5A";
    public const string DefaultHeader = "#15181C";

    /// <summary>Returns the colour if it is a plain #RRGGBB value, otherwise the fallback.</summary>
    public static string SafeColor(string? value, string fallback) =>
        value is not null && HexColor().IsMatch(value.Trim()) ? value.Trim() : fallback;

    /// <summary>Very light tint of a colour (for selected rows, soft backgrounds).</summary>
    public static string Tint(string hex, double towardWhite = 0.88)
    {
        var (r, g, b) = Rgb(hex);
        int Mix(int c) => (int)Math.Round(c + (255 - c) * towardWhite);
        return $"#{Mix(r):X2}{Mix(g):X2}{Mix(b):X2}";
    }

    /// <summary>Darker shade (hover states).</summary>
    public static string Shade(string hex, double towardBlack = 0.25)
    {
        var (r, g, b) = Rgb(hex);
        int Mix(int c) => (int)Math.Round(c * (1 - towardBlack));
        return $"#{Mix(r):X2}{Mix(g):X2}{Mix(b):X2}";
    }

    /// <summary>WCAG contrast ratio of white text on this colour (4.5 or more is readable).</summary>
    public static double ContrastWithWhite(string hex)
    {
        var (r, g, b) = Rgb(hex);
        static double Lin(int c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
        return 1.05 / (luminance + 0.05);
    }

    private static (int R, int G, int B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        return (int.Parse(h[..2], NumberStyles.HexNumber), int.Parse(h[2..4], NumberStyles.HexNumber), int.Parse(h[4..6], NumberStyles.HexNumber));
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")] private static partial Regex HexColor();
}
