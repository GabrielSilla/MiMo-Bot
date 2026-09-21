namespace Brobot.Sender;

/// <summary>
/// Tells apart "a browser tab is playing YouTube" from "a browser tab is
/// playing something else" — WindowsMediaMonitor's own SMTC session only
/// ever exposes which *app* is playing (Spotify, Edge, Chrome, VLC, ...),
/// never which site inside a browser, since that's the browser's own
/// business, not the OS's. The actual tab walk lives in
/// ChromiumTabFocusFinder (shared with SocialMediaTabDetector); this class
/// only owns the YouTube-specific matching and its own header knowledge.
///
/// Confirmed live (not assumed): a Chromium tab's own accessible Name
/// carries an audio-playing descriptor for as long as that tab is actually
/// producing sound, appended the same way Edge already appends "-
/// Gravação de microfone" to a live call's tab (see LiveCallApps.Edge's
/// own comment) — so a YouTube tab's title containing "YouTube" is already
/// gated on it actually being what SMTC is reporting as playing, not a
/// stale tab sitting open and paused. Each TabItem also exposes
/// SelectionItemPattern.IsSelected, confirmed live to stay accurate for a
/// background tab too (tested by switching to a second tab in the same
/// window: the YouTube tab kept its audio-playing suffix but IsSelected
/// flipped to false) — which is what lets MiMo say "tocando ao fundo"
/// instead of silently treating background playback the same as actively
/// watching.
///
/// Edge/Chrome phrase that descriptor as "<title> - YouTube - Áudio em
/// reprodução - Uso de memória - N MB"; Brave (also confirmed live, same
/// mechanism, different wording) instead reads "Uso da memória em <title>
/// - YouTube - Reprodução de áudio: N MB" — memory-first instead of
/// audio-first, different phrase entirely for the audio cue. Neither
/// matters here: matching is a plain "YouTube" substring anywhere in the
/// name, so it holds regardless of how a given Chromium build orders or
/// words its own status suffixes.
///
/// Unlike SocialMediaTabDetector, this one only ever needs to run while
/// SMTC already reports a browser playing something (see
/// MainWindow.SendWatchingMessage) — YouTube reliably registers a media
/// session, so there's always an event to hang the check off, no
/// independent poll needed on its own (the 3s re-check timer that *does*
/// exist is only there to catch a plain tab switch, not to discover
/// playback SMTC never announced in the first place).
/// </summary>
internal static class YouTubeTabDetector
{
    // Brave is Chromium underneath, same accessibility tree shape (tabs,
    // selection pattern, an audio-playing descriptor in the tab name) as
    // Edge/Chrome — only its process name (and, confirmed live, its exact
    // status-suffix wording) differs.
    private static readonly string[] BrowserProcessNames = { "msedge", "chrome", "brave" };

    /// <summary>
    /// null = no YouTube tab found in any watched browser window (probably
    /// a different site, or WATCHING came from something that isn't a
    /// browser at all, e.g. VLC). Otherwise: whether that tab is the
    /// window's currently selected one.
    /// </summary>
    public static bool? TryFindFocused() =>
        ChromiumTabFocusFinder.TryFind(BrowserProcessNames,
            name => name.IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) >= 0);
}
