using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Brobot.Sender;

/// <summary>
/// Which shape of the microphone consent-store key an app uses -- see
/// LiveCallMonitor.IsMicActive's own comment for what was confirmed live
/// for each. Packaged apps (MSIX, e.g. Teams) are keyed directly by their
/// PackageFamilyName; plain Win32 apps (e.g. any browser) live under a
/// NonPackaged subkey, keyed by their own exe path with '\' swapped for '#'.
/// </summary>
public enum MicRegistryKind
{
    Packaged,
    NonPackaged,
}

/// <summary>
/// Everything that differs between one watched app and the next -- the rest
/// (poll timing, the mic-active/in-call state machine, the registry read)
/// is identical and lives once in LiveCallMonitor. TryFindActiveCallLabel is
/// deliberately a delegate rather than more structured config: Teams' own
/// call window and a browser's Meet tab are found by genuinely different
/// mechanisms (see DesktopWindowCallFinder/BrowserTabCallFinder below), and
/// forcing both through one shape would just relocate the per-app branching
/// rather than remove it. See LiveCallApps for the concrete definitions.
/// </summary>
public sealed record LiveCallAppDefinition(
    string Name,
    // What MiMo actually says the call is happening "in" -- e.g. "Teams" or
    // "Navegador". Kept separate from Name because Name doubles as the
    // per-app tracking key (Edge and Chrome must stay independent entries so
    // one ending doesn't clear the other), while Edge and Chrome both read
    // the same to the user: they're both just "the browser".
    string SourceLabel,
    string[] ProcessNames,
    MicRegistryKind MicRegistryKind,
    string? PackageFamilyName,
    Func<string?> TryFindActiveCallLabel);

/// <summary>
/// Ready-made LiveCallAppDefinitions for the apps Notificações currently
/// watches. Adding a future app (Zoom, Slack huddles, ...) is one more entry
/// here -- a packaged desktop app reuses DesktopWindowCallFinder the way
/// Teams does, a browser-hosted one reuses BrowserTabCallFinder the way
/// Edge/Chrome do, and only a genuinely new shape needs a third finder.
/// </summary>
public static class LiveCallApps
{
    /// <summary>
    /// Confirmed live: mid-call, "ms-teams"'s MSIX package
    /// (MSTeams_8wekyb3d8bbwe) reads LastUsedTimeStop == 0 in the consent
    /// store, and the call itself is a real, separate, visible top-level
    /// window (same TeamsWebView class as the main window and any chat
    /// window) whose title reads "&lt;label&gt; | Microsoft Teams" -- an
    /// ad-hoc call's label was Teams' own generic internal string
    /// ("PrintService"), accepted as-is per explicit product decision rather
    /// than chased further; a calendar-scheduled meeting's label may read
    /// differently, not yet confirmed against a real one. Chat windows share
    /// the same "| Microsoft Teams" suffix but are excluded by their own
    /// "Chat | ..." prefix.
    /// </summary>
    public static LiveCallAppDefinition Teams { get; } = new(
        Name: "Teams",
        SourceLabel: "Teams",
        ProcessNames: new[] { "ms-teams" },
        MicRegistryKind: MicRegistryKind.Packaged,
        PackageFamilyName: "MSTeams_8wekyb3d8bbwe",
        TryFindActiveCallLabel: () => DesktopWindowCallFinder.TryFindLabel(
            new[] { "ms-teams" },
            title => title.Contains("| Microsoft Teams", StringComparison.Ordinal)
                      && !title.StartsWith("Chat |", StringComparison.Ordinal),
            title => title[..title.IndexOf('|')].Trim()));

    /// <summary>
    /// Confirmed live during a real Google Meet call: Edge's own
    /// NonPackaged consent-store entry (keyed by its exe path) reads
    /// LastUsedTimeStop == 0 the same way Teams' packaged one does, and --
    /// unlike Teams, where the call gets its own top-level window -- a Meet
    /// tab is just one tab among Edge's ordinary windows, so it's found via
    /// its own UI Automation TabItem rather than EnumWindows/GetWindowText
    /// (see BrowserTabCallFinder). Its accessible Name was observed as
    /// "Meet: kmt-bgya-pix - Gravação de microfone - Uso de memória - 316
    /// MB" -- Edge appends live status/memory descriptors after the tab's
    /// own title, separated by " - ", so splitting on that and keeping the
    /// first segment recovers the plain tab title regardless of which
    /// descriptors happen to be attached at that moment.
    /// </summary>
    public static LiveCallAppDefinition Edge { get; } = new(
        Name: "Edge",
        SourceLabel: "Navegador",
        ProcessNames: new[] { "msedge" },
        MicRegistryKind: MicRegistryKind.NonPackaged,
        PackageFamilyName: null,
        TryFindActiveCallLabel: () => BrowserTabCallFinder.TryFindLabel(
            new[] { "msedge" },
            name => name.Contains("Meet:", StringComparison.Ordinal),
            ExtractMeetTabLabel));

    /// <summary>
    /// Same mechanism as Edge (both are Chromium, so the same UI Automation
    /// tab-strip shape applies) -- not yet confirmed live against a real
    /// Chrome call, but there is no reason to expect it to differ from Edge
    /// beyond the process name.
    /// </summary>
    public static LiveCallAppDefinition Chrome { get; } = new(
        Name: "Chrome",
        SourceLabel: "Navegador",
        ProcessNames: new[] { "chrome" },
        MicRegistryKind: MicRegistryKind.NonPackaged,
        PackageFamilyName: null,
        TryFindActiveCallLabel: () => BrowserTabCallFinder.TryFindLabel(
            new[] { "chrome" },
            name => name.Contains("Meet:", StringComparison.Ordinal),
            ExtractMeetTabLabel));

    /// <summary>
    /// "Meet: kmt-bgya-pix - Gravação de microfone - ..." -&gt; "kmt-bgya-pix".
    /// The tab's own "Meet: " prefix is dropped too, not just the trailing
    /// status descriptors -- MainWindow already prefixes the sent text with
    /// "Em reunião: ", so keeping "Meet: " here produced a redundant-looking
    /// "Em reunião: Meet: kmt-bgya-pix".
    /// </summary>
    private static string ExtractMeetTabLabel(string tabName)
    {
        string title = tabName.Split(" - ")[0].Trim();
        const string meetPrefix = "Meet:";
        if (title.StartsWith(meetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            title = title[meetPrefix.Length..].Trim();
        }
        return title;
    }
}

/// <summary>
/// Detects "you're in a live call right now" for one configured app and
/// raises CallChanged with a best-effort label when it starts (and null when
/// it ends), so MainWindow can hold FACE MEETING / "Em reunião: &lt;label&gt;"
/// on screen for the call's whole duration -- the same sticky "now playing"
/// treatment WindowsMediaMonitor already gives MUSIC/WATCHING. A different
/// signal from everything else Notificações already watches:
/// NotificationMonitor/TeamsNotificationWatcher both react to a *toast
/// appearing*, this reacts to the call itself actually being live,
/// independent of whether any toast ever fired for it.
///
/// Originally written Teams-only (mic-in-use + Teams' own call window); once
/// the same two-signal shape (mic-in-use, then a matching window/tab) turned
/// out to also hold for Google Meet in a browser tab, confirmed live the
/// same way, this was generalized to take a LiveCallAppDefinition instead of
/// hardcoding Teams -- one instance per watched app (see LiveCallApps),
/// rather than a second near-identical copy of this whole class.
/// </summary>
public sealed class LiveCallMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private const string MicConsentKeyRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private readonly LiveCallAppDefinition _app;
    private DispatcherTimer? _timer;

    // Tracks whether CallChanged has already fired for the call currently
    // underway -- Poll() runs every 20s for as long as the mic stays in use,
    // and without this it would re-raise the same call's label on every tick
    // instead of once when it starts.
    private bool _inCall;

    /// <summary>
    /// Raised with the call's label when it starts, and with null when it
    /// ends -- same "one event, null means nothing anymore" shape
    /// WindowsMediaMonitor's own NowPlayingChanged already uses.
    /// </summary>
    public event Action<string?>? CallChanged;

    public LiveCallMonitor(LiveCallAppDefinition app)
    {
        _app = app;
    }

    public void Start()
    {
        if (_timer != null)
        {
            return;
        }
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _inCall = false;
    }

    public void Dispose() => Stop();

    private void Poll()
    {
        if (!IsMicActive())
        {
            // The mic going quiet is what re-arms detection for the *next*
            // call -- without resetting this here, only the very first call
            // in a session would ever raise CallChanged again. Only actually
            // raise the "ended" event if a start was raised for it in the
            // first place, same "don't fire a stop nobody's waiting for"
            // guard WindowsMediaMonitor's own RaiseIfChanged already follows.
            if (_inCall)
            {
                _inCall = false;
                CallChanged?.Invoke(null);
            }
            return;
        }

        if (_inCall)
        {
            return;
        }

        string? label = _app.TryFindActiveCallLabel();
        if (label == null)
        {
            // Mic active but no distinguishable call window/tab yet -- e.g.
            // the call is still connecting. Left un-armed so the next tick
            // tries again rather than treating this as "no call, ever".
            return;
        }

        _inCall = true;
        CallChanged?.Invoke(label);
    }

    /// <summary>
    /// Stop == 0 while Start is set means "still in use" -- confirmed live
    /// for both registry shapes before this class trusted either: Teams'
    /// packaged entry (MSTeams_8wekyb3d8bbwe) read stop=0 mid-call, and
    /// Edge's NonPackaged entry (keyed by its own exe path) read stop=0
    /// mid-Meet-call the same way.
    /// </summary>
    private bool IsMicActive()
    {
        try
        {
            string? keyPath = _app.MicRegistryKind switch
            {
                MicRegistryKind.Packaged => $@"{MicConsentKeyRoot}\{_app.PackageFamilyName}",
                MicRegistryKind.NonPackaged => BuildNonPackagedKeyPath(),
                _ => null,
            };
            if (keyPath == null)
            {
                return false;
            }

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key == null)
            {
                return false;
            }
            // REG_QWORD values -- GetValue already boxes them as long, no
            // conversion needed beyond the pattern match itself.
            return key.GetValue("LastUsedTimeStart") is long start
                && start != 0
                && key.GetValue("LastUsedTimeStop") is long stop
                && stop == 0;
        }
        catch (Exception)
        {
            // A locked-down machine, a future Windows build moving this key,
            // whatever -- this is best-effort telemetry, never something
            // worth taking the poll loop down over.
            return false;
        }
    }

    /// <summary>
    /// A NonPackaged app is keyed by its own exe path (backslashes swapped
    /// for '#'), not a fixed identifier the way a packaged app's
    /// PackageFamilyName is -- so this resolves it fresh from whichever of
    /// the app's process names is actually running, rather than baking a
    /// path into LiveCallAppDefinition that would go stale the moment the
    /// browser is installed somewhere else.
    /// </summary>
    private string? BuildNonPackagedKeyPath()
    {
        string? exePath = null;
        foreach (string processName in _app.ProcessNames)
        {
            try
            {
                using Process? proc = Process.GetProcessesByName(processName).FirstOrDefault();
                exePath = proc?.MainModule?.FileName;
            }
            catch (Exception)
            {
                // MainModule throws for a handful of protected/elevated
                // processes -- treat exactly like "not running".
            }
            if (exePath != null)
            {
                break;
            }
        }
        if (exePath == null)
        {
            return null;
        }
        string sanitized = exePath.Replace('\\', '#');
        return $@"{MicConsentKeyRoot}\NonPackaged\{sanitized}";
    }
}

/// <summary>
/// Shared Win32 window enumeration -- the same EnumWindows/GetWindowText/
/// GetWindowThreadProcessId/IsWindowVisible P/Invoke set this project's
/// TeamsNotificationWatcher (a different problem: catching a notification
/// popup's SHOW event) and the original Teams-only meeting monitor both
/// already needed on their own. Centralized here so DesktopWindowCallFinder
/// and BrowserTabCallFinder (which also needs a window handle before it can
/// walk that window's UI Automation tree) share one copy instead of two.
/// </summary>
internal static class Win32Windows
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    internal static List<IntPtr> FindVisibleTopLevelWindows(IReadOnlySet<uint> pids)
    {
        var found = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pids.Contains(pid) && IsWindowVisible(hWnd))
            {
                found.Add(hWnd);
            }
            return true; // keep enumerating -- collect every match, not just the first
        }, IntPtr.Zero);
        return found;
    }

    internal static string GetTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static HashSet<uint> PidsFor(IEnumerable<string> processNames)
    {
        return processNames
            .SelectMany(Process.GetProcessesByName)
            .Select(p => (uint)p.Id)
            .ToHashSet();
    }
}

/// <summary>
/// Finds a live call by its own real, visible, top-level window -- the shape
/// Teams uses (see LiveCallApps.Teams's own comment for what was confirmed
/// live: a distinct TeamsWebView window per call, told apart from the main
/// window and any chat window purely by title). Reusable as-is for any
/// future desktop app whose calls each open their own window.
/// </summary>
internal static class DesktopWindowCallFinder
{
    public static string? TryFindLabel(
        string[] processNames,
        Func<string, bool> isCallWindowTitle,
        Func<string, string> extractLabel)
    {
        HashSet<uint> pids = Win32Windows.PidsFor(processNames);
        if (pids.Count == 0)
        {
            return null;
        }

        foreach (IntPtr hWnd in Win32Windows.FindVisibleTopLevelWindows(pids))
        {
            string title = Win32Windows.GetTitle(hWnd);
            if (title.Length > 0 && isCallWindowTitle(title))
            {
                return extractLabel(title);
            }
        }
        return null;
    }
}

/// <summary>
/// Finds a live call inside a browser tab -- unlike Teams, a Meet tab is not
/// its own top-level window, it's one TabItem among the browser's ordinary
/// windows, so this walks each matching window's UI Automation tree looking
/// for a TabItem whose accessible Name matches, rather than comparing window
/// titles directly (a browser window's own title only ever reflects
/// whichever tab currently has focus, which a call tab may not). Confirmed
/// live for Edge/Google Meet -- see LiveCallApps.Edge's own comment.
/// </summary>
internal static class BrowserTabCallFinder
{
    public static string? TryFindLabel(
        string[] processNames,
        Func<string, bool> isCallTabName,
        Func<string, string> extractLabel)
    {
        HashSet<uint> pids = Win32Windows.PidsFor(processNames);
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
                if (name.Length > 0 && isCallTabName(name))
                {
                    return extractLabel(name);
                }
            }
        }
        return null;
    }
}
