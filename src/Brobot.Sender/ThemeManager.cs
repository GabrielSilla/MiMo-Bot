using System.Windows;

namespace Brobot.Sender;

/// <summary>
/// Swaps MiMo's color palette at runtime by merging one theme's
/// ResourceDictionary into Application.Resources in place of whichever one
/// was there before. Every color in MainWindow/SettingsWindow is looked up
/// via DynamicResource against the brush keys defined in Themes/*.xaml
/// (WindowBackgroundBrush, InkBrush, CardBrush, ...), so replacing the
/// dictionary here is enough to repaint any already-open window — no
/// restart, no per-window theming code.
/// </summary>
public static class ThemeManager
{
    /// <summary>
    /// CoreTheme is the value this app's own "Tema" selection sends as
    /// `THEME &lt;CoreTheme&gt;` to Core (see PROTOCOL.md) — a second,
    /// unrelated system this same picker now also drives, alongside
    /// ResourcePath's WPF skin. They just happen to both be "appearance".
    /// </summary>
    public sealed record ThemeInfo(string Key, string DisplayName, string ResourcePath, string CoreTheme);

    public const string DefaultTheme = "MiMoClassic";

    public static readonly IReadOnlyList<ThemeInfo> Available =
    [
        new ThemeInfo(DefaultTheme, "MiMo Classic", "Themes/MiMoClassic.xaml", "DEFAULT"),
        // Reuses MiMoClassic.xaml — there's no dedicated Matrix WPF skin for
        // this app's own UI yet, only for Core's display; selecting this
        // entry doesn't change how Brobot.Sender itself looks, just what
        // THEME command goes out to Core.
        new ThemeInfo("MiMoMatrix", "MiMo Matrix", "Themes/MiMoClassic.xaml", "MATRIX"),
    ];

    private static ResourceDictionary? _activeThemeDictionary;

    public static void Apply(string? themeKey)
    {
        ThemeInfo theme = Available.FirstOrDefault(t => t.Key == themeKey)
            ?? Available.First(t => t.Key == DefaultTheme);

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Brobot.Sender;component/{theme.ResourcePath}", UriKind.Absolute),
        };

        ResourceDictionary appResources = System.Windows.Application.Current.Resources;
        if (_activeThemeDictionary != null)
        {
            appResources.MergedDictionaries.Remove(_activeThemeDictionary);
        }

        appResources.MergedDictionaries.Add(dictionary);
        _activeThemeDictionary = dictionary;
    }
}
