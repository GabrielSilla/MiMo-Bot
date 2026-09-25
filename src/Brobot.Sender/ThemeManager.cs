using System.Windows;

namespace Brobot.Sender;

/// <summary>
/// Swaps Peemo's color palette at runtime by merging one theme's
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

    public const string DefaultTheme = "PeemoClassic";

    public static readonly IReadOnlyList<ThemeInfo> Available =
    [
        new ThemeInfo(DefaultTheme, "Peemo Classic", "Themes/PeemoClassic.xaml", "DEFAULT"),
        // Reuses PeemoClassic.xaml — there's no dedicated Matrix WPF skin for
        // this app's own UI yet, only for Core's display; selecting this
        // entry doesn't change how Brobot.Sender itself looks, just what
        // THEME command goes out to Core.
        new ThemeInfo("PeemoMatrix", "Peemo Matrix", "Themes/PeemoClassic.xaml", "MATRIX"),
        // Same reasoning as Matrix above: reuses PeemoClassic.xaml, since
        // P2M2 only changes how Core's own display renders (red circle
        // eyes, red Aurebesh-translating message text — see PROTOCOL.md),
        // not this app's own UI.
        new ThemeInfo("PeemoP2M2", "Peemo P2-M2", "Themes/PeemoClassic.xaml", "P2M2"),
        // Same reasoning again: PEEMO84 is an amber-CRT terminal on Core's own
        // display (see PROTOCOL.md/Face.cpp), not a skin for this app.
        new ThemeInfo("Peemo84", "Peemo-84", "Themes/PeemoClassic.xaml", "PEEMO84"),
    ];

    /// <summary>
    /// CoreColor is the value sent as `CLASSICCOLOR &lt;CoreColor&gt;` (see
    /// PROTOCOL.md) — only meaningful while Peemo Classic itself is selected
    /// (Core ignores it entirely on every other theme, see Face.cpp's
    /// classicColorRGB), which is why MainWindow only shows this picker for
    /// that one entry above.
    /// </summary>
    public sealed record ClassicColorInfo(string Key, string DisplayName, string CoreColor);

    public const string DefaultClassicColor = "Blue";

    public static readonly IReadOnlyList<ClassicColorInfo> AvailableClassicColors =
    [
        new ClassicColorInfo("Blue", "Azul (original)", "BLUE"),
        // Green/Amber deliberately reuse the exact wire values Peemo Matrix/
        // Peemo-84 already send via THEME, not new ones of their own — see
        // Face.cpp's classicColorRGB for why.
        new ClassicColorInfo("Green", "Verde (Matrix)", "GREEN"),
        new ClassicColorInfo("Amber", "Âmbar (Peemo-84)", "AMBER"),
        new ClassicColorInfo("Red", "Vermelho", "RED"),
        new ClassicColorInfo("Pink", "Rosa", "PINK"),
        new ClassicColorInfo("White", "Branco", "WHITE"),
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
