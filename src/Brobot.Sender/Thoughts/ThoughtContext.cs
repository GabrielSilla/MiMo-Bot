namespace Brobot.Sender.Thoughts;

/// <summary>
/// A snapshot of what Sender already knows at the moment a thought is due —
/// built by MainWindow from state it keeps anyway, so sources never reach
/// into the UI. The *Enabled flags carry the cards' gating: dev triggers
/// need Ferramentas de Dev, social/YouTube need Mídia, games need Jogos,
/// meetings need Notificações (that's what watches live calls).
/// </summary>
internal sealed record ThoughtContext(
    DateTime Now,
    Mood Mood,
    DailyReportResult Report,
    IReadOnlyDictionary<string, double> SocialMinutesBySite,
    IReadOnlyDictionary<string, double> GameMinutesByName,
    WeatherReading? Weather,
    bool DevToolsEnabled,
    bool MediaEnabled,
    bool GamesEnabled,
    bool NotificationsEnabled,
    string? LastGameName,
    DateTime? LastGameEndedAt,
    DateTime? LastMeetingEndedAt);
