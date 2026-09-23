using System.Windows.Automation;

namespace Brobot.Sender;

/// <summary>
/// Shared UI Automation tab walk behind both YouTubeTabDetector and
/// SocialMediaTabDetector — same "which browser tab matches, and is it the
/// focused one" question, just against a different name pattern each time.
/// Factored out once a second caller needed the exact same walk rather than
/// a second copy of it: same "one shared implementation instead of one per
/// caller" reasoning LiveCallMonitor's own BrowserTabCallFinder already
/// follows for its two apps (Edge/Chrome), just one level up — this one
/// also needs SelectionItemPattern.IsSelected, which BrowserTabCallFinder's
/// own TryFindLabel never had to read.
///
/// "Focused" means the OS foreground window, not just a tab's own
/// SelectionItemPattern.IsSelected within whatever window it happens to sit
/// in — a real bug, reported directly: a Facebook tab can stay the
/// *selected* tab of its own browser window indefinitely while that whole
/// window sits in the background and some completely different app (an
/// IDE, a game, anything) is what the user is actually looking at. Only
/// the foreground window's own tabs are ever walked now — if the
/// foreground window doesn't even belong to one of the given browser
/// processes, this returns null immediately without walking anything, the
/// same answer as "no matching tab found" (a background browser tab being
/// selected doesn't count as watching/browsing it, so there's nothing to
/// distinguish here from genuinely not having one open).
/// </summary>
internal static class ChromiumTabFocusFinder
{
    /// <summary>
    /// null = the foreground window isn't one of the given browsers at
    /// all, or none of its tabs match `isMatch`. Otherwise: whether the
    /// first matching tab in the foreground window is its currently
    /// selected one.
    /// </summary>
    public static bool? TryFind(string[] processNames, Func<string, bool> isMatch)
    {
        nint foreground = Win32Windows.GetForegroundWindowHandle();
        if (foreground == 0)
        {
            return null;
        }

        HashSet<uint> pids = Win32Windows.PidsFor(processNames);
        if (pids.Count == 0 || !pids.Contains(Win32Windows.GetProcessId(foreground)))
        {
            return null;
        }

        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(foreground);
        }
        catch (Exception)
        {
            return null; // window closed between the check above and here, etc.
        }
        if (root == null)
        {
            return null;
        }

        var tabItemCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty, ControlType.TabItem);

        AutomationElementCollection tabs;
        try
        {
            tabs = root.FindAll(TreeScope.Descendants, tabItemCondition);
        }
        catch (Exception)
        {
            return null; // UI Automation can throw on a window mid-teardown
        }

        foreach (AutomationElement tab in tabs)
        {
            // FindAll only snapshots the tree — a tab closed, dragged out
            // or re-created by the browser in the meantime throws
            // ElementNotAvailableException on first property access here.
            // Confirmed live (sender-errors.log), and it surfaced as an
            // "Erro inesperado" dialog from the 5s social-media tick. Just
            // skip that tab; the next tick walks a fresh tree.
            try
            {
                string name = tab.Current.Name ?? string.Empty;
                if (name.Length == 0 || !isMatch(name))
                {
                    continue;
                }

                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? patternObj)
                    && patternObj is SelectionItemPattern selectionPattern)
                {
                    return selectionPattern.Current.IsSelected;
                }
                return false; // found a match but couldn't read its selection state — safer to say "not focused" than to claim it is
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }
        }

        return null;
    }
}
