using System.Windows.Automation;

namespace Brobot.Sender;

/// <summary>
/// Tells apart "a browser tab is playing YouTube" from "a browser tab is
/// playing something else" — WindowsMediaMonitor's own SMTC session only
/// ever exposes which *app* is playing (Spotify, Edge, Chrome, VLC, ...),
/// never which site inside a browser, since that's the browser's own
/// business, not the OS's. Same UI Automation tab walk LiveCallMonitor's
/// BrowserTabCallFinder already does for Meet tabs (see its own header
/// comment for why a tab, not a window, is what needs walking here) — this
/// is a separate, smaller class rather than a third case bolted onto that
/// one: a Meet tab is found by a mic-active gate that already limits how
/// often the walk runs, while this runs on every WATCHING media change and
/// has nothing to do with a call being live.
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
    public static bool? TryFindFocused()
    {
        HashSet<uint> pids = Win32Windows.PidsFor(BrowserProcessNames);
        if (pids.Count == 0)
        {
            return null;
        }

        var tabItemCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty, ControlType.TabItem);

        foreach (IntPtr hWnd in Win32Windows.FindVisibleTopLevelWindows(pids))
        {
            AutomationElement? root;
            try
            {
                root = AutomationElement.FromHandle(hWnd);
            }
            catch (Exception)
            {
                continue; // window closed between enumeration and here, etc.
            }
            if (root == null)
            {
                continue;
            }

            AutomationElementCollection tabs;
            try
            {
                tabs = root.FindAll(TreeScope.Descendants, tabItemCondition);
            }
            catch (Exception)
            {
                continue; // UI Automation can throw on a window mid-teardown
            }

            foreach (AutomationElement tab in tabs)
            {
                string name = tab.Current.Name ?? string.Empty;
                if (name.IndexOf("YouTube", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? patternObj)
                    && patternObj is SelectionItemPattern selectionPattern)
                {
                    return selectionPattern.Current.IsSelected;
                }
                return false; // found the tab but couldn't read its selection state — safer to say "not focused" than to claim it is
            }
        }

        return null;
    }
}
