namespace Brobot.Sender;

/// <summary>
/// Same tab-focus question as YouTubeTabDetector (see its own header
/// comment for the shared ChromiumTabFocusFinder walk and why Brave counts
/// as Chromium too), but for TikTok/Instagram/Facebook instead of YouTube —
/// kept as its own class, not a fourth pattern bolted onto
/// YouTubeTabDetector, because the two feed genuinely separate counters
/// (DailyReportTracker's "Tempo de Vídeo" vs "Tempo de Rede Social", each
/// with its own 15-minute nudge and score penalty — a product decision, not
/// a technical one: YouTube stays its own thing on purpose).
///
/// Confirmed live: TikTok/Instagram/Facebook all carry their own name
/// plainly in the tab's accessible Name ("... | TikTok", "Instagram (...)
/// ...", "(7) Facebook ..."), so a plain per-site substring match is enough
/// — no special-cased status suffix to key on this time.
///
/// Unlike YouTube, none of the three ever registered an SMTC session in
/// testing (confirmed live: scrolling a feed, or a TikTok/Reels video
/// autoplaying muted-by-default, raised nothing in
/// GlobalSystemMediaTransportControlsSessionManager.GetSessions() at all) —
/// there is no "a browser is playing something" event to hang this off the
/// way WindowsMediaMonitor's own NowPlayingChanged does for YouTube. So
/// this has to be polled independently instead (see MainWindow's own
/// _socialMediaTimer, started/stopped alongside Mídia's WindowsMediaMonitor
/// rather than driven by it).
/// </summary>
internal static class SocialMediaTabDetector
{
    private static readonly string[] BrowserProcessNames = { "msedge", "chrome", "brave" };

    // Checked in this order, first match wins — only matters if someone
    // somehow has two of these as the focused tab across different browser
    // windows at once, which ChromiumTabFocusFinder can't happen anyway
    // (one focused tab per OS at a time); listed order has no real effect
    // today.
    private static readonly string[] SitePatterns = { "TikTok", "Instagram", "Facebook" };

    /// <summary>
    /// The site name (e.g. "Facebook") of whichever of the three is
    /// currently the *focused* tab in a watched browser, or null if none
    /// of them is. One ChromiumTabFocusFinder walk per site rather than a
    /// single combined-pattern walk — three short walks over one shared
    /// site name is what lets the caller know *which* site matched, not
    /// just that one did.
    /// </summary>
    public static string? TryFindFocusedSite()
    {
        foreach (string site in SitePatterns)
        {
            if (ChromiumTabFocusFinder.TryFind(BrowserProcessNames,
                    name => name.IndexOf(site, StringComparison.OrdinalIgnoreCase) >= 0) == true)
            {
                return site;
            }
        }
        return null;
    }
}
