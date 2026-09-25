namespace Brobot.Sender;

/// <summary>
/// The project was called "MiMo" before it became Peemo. Everything in the
/// code was renamed, but an existing install still has the old names
/// written on disk — this is the one place that knows them, so an upgrade
/// keeps the user's settings, theme, achievements and Claude Code hook
/// instead of silently resetting them. Nothing here is ever *written* back:
/// old values are only read and translated to the new ones.
/// </summary>
internal static class LegacyNames
{
    /// <summary>%AppData%\Brobot file SenderSettings used before the rename.</summary>
    public const string SettingsFileName = "mimo-sender-settings.json";

    /// <summary>Hook scripts ClaudeCodeHookInstaller registered in ~/.claude/settings.json before the rename.</summary>
    public const string ClaudeHookScript = "mimo-claude-hook.ps1";
    public const string ClaudeStatusLineScript = "mimo-claude-statusline.ps1";

    private static readonly Dictionary<string, string> ThemeKeys = new(StringComparer.Ordinal)
    {
        ["MiMoClassic"] = "PeemoClassic",
        ["MiMoMatrix"] = "PeemoMatrix",
        ["MiMoMi2Mo2"] = "PeemoP2M2",
        ["MiMo84"] = "Peemo84",
    };

    private static readonly Dictionary<string, string> CoreThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MI2MO2"] = "P2M2",
        ["MI84"] = "PEEMO84",
    };

    /// <summary>A saved ThemeManager key, translated if it predates the rename.</summary>
    public static string ThemeKey(string key) => ThemeKeys.TryGetValue(key, out string? renamed) ? renamed : key;

    /// <summary>A saved THEME wire token (AchievementStore.ThemesUsed), translated if it predates the rename.</summary>
    public static string CoreTheme(string token) => CoreThemes.TryGetValue(token, out string? renamed) ? renamed : token;
}
