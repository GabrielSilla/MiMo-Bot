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
        // Same reasoning as Matrix above: reuses MiMoClassic.xaml, since
        // MI2MO2 only changes how Core's own display renders (red circle
        // eyes, red Aurebesh-translating message text — see PROTOCOL.md),
        // not this app's own UI.
        new ThemeInfo("MiMoMi2Mo2", "MiMo Mi2-Mo2", "Themes/MiMoClassic.xaml", "MI2MO2"),
        // Same reasoning again: MI84 is an amber-CRT terminal on Core's own
        // display (see PROTOCOL.md/Face.cpp), not a skin for this app.
        new ThemeInfo("MiMo84", "MiMo-84", "Themes/MiMoClassic.xaml", "MI84"),
    ];

    /// <summary>
    /// CoreColor is the value sent as `CLASSICCOLOR &lt;CoreColor&gt;` (see
    /// PROTOCOL.md) — only meaningful while MiMo Classic itself is selected
    /// (Core ignores it entirely on every other theme, see Face.cpp's
    /// classicColorRGB), which is why MainWindow only shows this picker for
    /// that one entry above.
    /// </summary>
    public sealed record ClassicColorInfo(string Key, string DisplayName, string CoreColor);

    public const string DefaultClassicColor = "Blue";

    public static readonly IReadOnlyList<ClassicColorInfo> AvailableClassicColors =
    [
        new ClassicColorInfo("Blue", "Azul (original)", "BLUE"),
        // Green/Amber deliberately reuse the exact wire values MiMo Matrix/
        // MiMo-84 already send via THEME, not new ones of their own — see
        // Face.cpp's classicColorRGB for why.
        new ClassicColorInfo("Green", "Verde (Matrix)", "GREEN"),
        new ClassicColorInfo("Amber", "Âmbar (MiMo-84)", "AMBER"),
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
