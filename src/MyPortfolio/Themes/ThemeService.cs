using System.Windows;
using System.Windows.Media;
using MyPortfolio.Common;

namespace MyPortfolio.Themes;

public static class ThemeService
{
    public static readonly string[] ThemeFlavors = ["Mocha", "Latte"];
    public static readonly string[] AccentNames = ["Mauve", "Sapphire", "Teal", "Green", "Peach", "Red"];

    private static readonly ThemePalette Mocha = new(
        Base: "#1e1e2e", Mantle: "#181825", Crust: "#11111b",
        Surface0: "#313244", Surface1: "#45475a", Surface2: "#585b70",
        Overlay: "#6c7086", Text: "#cdd6f4", Subtext: "#a6adc8", Mute: "#7f849c",
        OnAccent: "#11111b",
        Colors: new Dictionary<string, AccentPalette>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mauve"] = new("#cba6f7", "#3b3052"),
            ["Sapphire"] = new("#74c7ec", "#243b4a"),
            ["Teal"] = new("#94e2d5", "#1f3b39"),
            ["Green"] = new("#a6e3a1", "#233d2d"),
            ["Peach"] = new("#fab387", "#4a2f24"),
            ["Red"] = new("#f38ba8", "#4b2734"),
            ["Yellow"] = new("#f9e2af", "#45351f")
        });

    private static readonly ThemePalette Latte = new(
        Base: "#eff1f5", Mantle: "#e6e9ef", Crust: "#dce0e8",
        Surface0: "#ccd0da", Surface1: "#bcc0cc", Surface2: "#acb0be",
        Overlay: "#9ca0b0", Text: "#4c4f69", Subtext: "#6c6f85", Mute: "#8c8fa1",
        OnAccent: "#eff1f5",
        Colors: new Dictionary<string, AccentPalette>(StringComparer.OrdinalIgnoreCase)
        {
            ["Mauve"] = new("#8839ef", "#eadcfb"),
            ["Sapphire"] = new("#209fb5", "#d7eef4"),
            ["Teal"] = new("#179299", "#d8f0ed"),
            ["Green"] = new("#40a02b", "#dcefd9"),
            ["Peach"] = new("#fe640b", "#fde2cf"),
            ["Red"] = new("#d20f39", "#f7d8df"),
            ["Yellow"] = new("#df8e1d", "#faecd3")
        });

    public static string NormalizeTheme(string? theme)
        => ThemeFlavors.FirstOrDefault(t => string.Equals(t, theme, StringComparison.OrdinalIgnoreCase)) ?? "Mocha";

    public static string NormalizeAccent(string? accent)
        => AccentNames.FirstOrDefault(a => string.Equals(a, accent, StringComparison.OrdinalIgnoreCase)) ?? "Mauve";

    public static void Apply(AppSettings settings)
    {
        settings.ThemeFlavor = NormalizeTheme(settings.ThemeFlavor);
        settings.AccentColor = NormalizeAccent(settings.AccentColor);
        Apply(settings.ThemeFlavor, settings.AccentColor);
    }

    private static void Apply(string themeFlavor, string accentName)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var palette = string.Equals(themeFlavor, "Latte", StringComparison.OrdinalIgnoreCase) ? Latte : Mocha;
        var accent = palette.Colors[accentName];

        SetPair(resources, "Base", palette.Base);
        SetPair(resources, "Mantle", palette.Mantle);
        SetPair(resources, "Crust", palette.Crust);
        SetPair(resources, "Surface0", palette.Surface0);
        SetPair(resources, "Surface1", palette.Surface1);
        SetPair(resources, "Surface2", palette.Surface2);
        SetPair(resources, "Overlay", palette.Overlay);
        SetPair(resources, "Text", palette.Text);
        SetPair(resources, "Subtext", palette.Subtext);
        SetPair(resources, "Mute", palette.Mute);
        SetPair(resources, "OnAccent", palette.OnAccent);

        SetPair(resources, "Mauve", accent.Primary);
        SetPair(resources, "MauveSoft", accent.Soft);
        SetPair(resources, "Sapphire", palette.Colors["Sapphire"].Primary);
        SetPair(resources, "SapphireSoft", palette.Colors["Sapphire"].Soft);
        SetPair(resources, "Teal", palette.Colors["Teal"].Primary);
        SetPair(resources, "TealSoft", palette.Colors["Teal"].Soft);
        SetPair(resources, "Green", palette.Colors["Green"].Primary);
        SetPair(resources, "GreenSoft", palette.Colors["Green"].Soft);
        SetPair(resources, "Peach", palette.Colors["Peach"].Primary);
        SetPair(resources, "Red", palette.Colors["Red"].Primary);
        SetPair(resources, "RedSoft", palette.Colors["Red"].Soft);
        SetPair(resources, "Yellow", palette.Colors["Yellow"].Primary);
        SetPair(resources, "YellowSoft", palette.Colors["Yellow"].Soft);
        SetBrush(resources, "FocusRingBrush", ToColor(accent.Primary));
    }

    private static void SetPair(ResourceDictionary resources, string token, string hex)
    {
        var color = ToColor(hex);
        SetColor(resources, $"{token}Color", color);
        SetBrush(resources, $"{token}Brush", color);
    }

    private static void SetColor(ResourceDictionary resources, string key, Color color)
    {
        var owner = FindOwner(resources, key) ?? resources;
        owner[key] = color;
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        var owner = FindOwner(resources, key) ?? resources;
        if (owner[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        owner[key] = new SolidColorBrush(color);
    }

    private static ResourceDictionary? FindOwner(ResourceDictionary dictionary, string key)
    {
        if (dictionary.Contains(key)) return dictionary;
        foreach (var merged in dictionary.MergedDictionaries)
        {
            var owner = FindOwner(merged, key);
            if (owner is not null) return owner;
        }
        return null;
    }

    private static Color ToColor(string hex)
        => (Color)ColorConverter.ConvertFromString(hex);

    private sealed record AccentPalette(string Primary, string Soft);

    private sealed record ThemePalette(
        string Base,
        string Mantle,
        string Crust,
        string Surface0,
        string Surface1,
        string Surface2,
        string Overlay,
        string Text,
        string Subtext,
        string Mute,
        string OnAccent,
        IReadOnlyDictionary<string, AccentPalette> Colors);
}
