using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Brobot.Sender;

/// <summary>
/// Captures Microsoft Teams' own in-app notification banner, which
/// NotificationMonitor's UserNotificationListener genuinely cannot see:
/// confirmed empirically (not assumed) that Teams never registers a
/// Windows toast for it at all -- it renders the banner as ordinary
/// WebView2/Chromium DOM content inside a small popup window of its own,
/// separate from Teams' huge main window but sharing its process and
/// window class ("TeamsWebView").
///
/// This is a deliberately more fragile capture path than
/// NotificationMonitor's, built only after confirming the "proper" WinRT
/// route has no visibility into Teams at all -- it depends on: Teams
/// staying a WebView2 app, the popup staying small relative to the main
/// window, and (worse) the *exact PT-BR accessibility strings* Teams
/// happens to render on this machine today. A Teams update, a different
/// display language, or a UI redesign can silently break this with no
/// error -- it just stops finding text and nothing shows up. Accepted
/// tradeoff, not an oversight (see this project's CLAUDE.md).
///
/// Mechanism: a global SetWinEventHook for EVENT_OBJECT_SHOW (the same
/// primitive every real accessibility tool is built on) filtered down to
/// "a small TeamsWebView window just became visible" -- distinguishing the
/// notification popup from Teams' own main window purely by width, since
/// the main window is always far wider than any monitor's small corner
/// popup. UserNotificationListener's own NotificationChanged push event
/// needed package identity and failed outright (COMException 0x80070109);
/// this global hook does not, and needs no packaging at all.
/// </summary>
public sealed class TeamsNotificationWatcher : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjIdWindow = 0;

    // Teams' main window is always far wider than this on any real
    // monitor; the notification popup measured ~372px during testing.
    // Width alone is enough to tell them apart without also matching
    // window title (which is just "Microsoft Teams" for both).
    private const int MaxPopupWidthPx = 800;

    // Teams renders a screen-reader-only composite label ("Visualização
    // da mensagem. <body> <app name>") as a sibling of the plain body
    // text node -- without dropping it, extracted text would repeat
    // itself. PT-BR specific: this machine's Teams UI language. See this
    // class's own header comment for why that's an accepted fragility,
    // not something worth generalizing without real evidence of other
    // locales' actual strings.
    private const string MessagePreviewPrefix = "Visualização da mensagem";
    private const string TeamsAppName = "Microsoft Teams";

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    // Kept as a field, never a local: SetWinEventHook does not root the
    // delegate on its own, and Windows calling into a GC'd delegate
    // crashes the process the next time the hook fires.
    private readonly WinEventDelegate _callback;
    private IntPtr _hookHandle = IntPtr.Zero;

    // The same banner re-fires SHOW a few times while it animates in/out
    // (observed directly while building this) -- without this, one Teams
    // notification would turn into several duplicate NOTIFYs on MiMo.
    private string? _lastText;
    private DateTime _lastRaisedAtUtc = DateTime.MinValue;
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(3);

    /// <summary>Raised on the same thread Start() was called on (see Start's own doc) -- reuses NotificationMonitor's PcNotification shape so MainWindow can share one handler for both sources.</summary>
    public event Action<PcNotification>? NotificationReceived;

    public TeamsNotificationWatcher()
    {
        _callback = OnWinEvent;
    }

    /// <summary>
    /// Must be called from a thread that pumps Windows messages (the WPF
    /// UI thread qualifies) -- SetWinEventHook with WINEVENT_OUTOFCONTEXT
    /// delivers callbacks through the calling thread's own message queue,
    /// so calling this from a bare background Task with no message loop
    /// would install the hook but silently never invoke the callback.
    /// </summary>
    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }
        // idProcess/idThread = 0: a global hook, not scoped to Teams' PID at
        // installation time. Teams can restart with a new PID at any point
        // while this checkbox stays checked, so the callback re-checks the
        // owning process by name on every event instead of filtering by a
        // PID captured once here.
        _hookHandle = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext);
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // This fires for every window-level SHOW event system-wide (the
        // idObject check alone already discards most non-window
        // accessibility objects) -- everything below is about cheaply
        // discarding the near-totality of events that aren't Teams'
        // notification popup, same idea GlobalKeyboardHook already applies
        // to every keystroke on the system.
        if (hwnd == IntPtr.Zero || idObject != ObjIdWindow)
        {
            return;
        }

        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            using Process proc = Process.GetProcessById((int)pid);
            if (!string.Equals(proc.ProcessName, "ms-teams", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!GetWindowRect(hwnd, out Rect rect) || (rect.Right - rect.Left) >= MaxPopupWidthPx)
            {
                return; // Teams' own main window, not the notification popup
            }

            AutomationElement popup = AutomationElement.FromHandle(hwnd);
            string text = ExtractNotificationText(popup);
            if (text.Length == 0)
            {
                return;
            }

            if (text == _lastText && DateTime.UtcNow - _lastRaisedAtUtc < DedupeWindow)
            {
                return;
            }
            _lastText = text;
            _lastRaisedAtUtc = DateTime.UtcNow;

            NotificationReceived?.Invoke(new PcNotification(TeamsAppName, text));
        }
        catch
        {
            // The process already exited, the window was already destroyed,
            // or the UIA call raced against Teams tearing the popup down --
            // this callback fires constantly for unrelated system windows
            // too, so any failure here just means "not a Teams notification
            // popup", never something worth surfacing.
        }
    }

    /// <summary>
    /// Walks down to the popup's WebView2 content root, then reads every
    /// Text-typed descendant's accessible Name -- confirmed by hand which
    /// nodes carry real content vs. Teams' own redundant screen-reader
    /// labels (see MessagePreviewPrefix/TeamsAppName above).
    /// </summary>
    private static string ExtractNotificationText(AutomationElement popupRoot)
    {
        AutomationElement? doc = popupRoot.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        if (doc == null)
        {
            return string.Empty;
        }

        AutomationElementCollection textEls = doc.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));

        var parts = new List<string>();
        foreach (AutomationElement el in textEls)
        {
            string name = el.Current.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            if (name == TeamsAppName)
            {
                continue;
            }
            if (name.StartsWith(MessagePreviewPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!parts.Contains(name))
            {
                parts.Add(name);
            }
        }
        return string.Join(" - ", parts);
    }
}
