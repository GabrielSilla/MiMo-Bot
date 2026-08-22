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
    public sealed record ThemeInfo(string Key, string DisplayName, string ResourcePath);

    public const string DefaultTheme = "MiMoClassic";

    public static readonly IReadOnlyList<ThemeInfo> Available =
    [
        new ThemeInfo(DefaultTheme, "MiMo Classic", "Themes/MiMoClassic.xaml"),
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
