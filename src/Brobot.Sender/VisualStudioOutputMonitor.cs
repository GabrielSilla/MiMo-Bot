using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Brobot.Sender;

/// <summary>
/// Detects a Visual Studio build starting/finishing by reading VS's own
/// Output window (the "Build" channel of the Saída/Output tool pane) via UI
/// Automation -- purely passive, external text reads, the same technique
/// TeamsNotificationWatcher already uses safely for Teams. Deliberately NOT
/// COM automation (DTE): two separate attempts at that (one with no message
/// filter, one with a proper IOleMessageFilter registered) both hung Visual
/// Studio itself the moment a build started, reported directly both times.
/// This reads text VS already renders on its own, the same way a person
/// looking at the screen would -- no call ever crosses into devenv.exe's
/// own automation object model or STA apartment.
///
/// Confirmed live, not assumed: VS's Output/Build pane is a WpfTextView
/// (AutomationId "WpfTextView", the same editor control VS's code windows
/// use) nested under a toolbar named "Output", and its TextPattern gives
/// the exact console transcript a terminal build would show, including the
/// per-project start line (confirmed live against a real PT-BR Visual
/// Studio instance mid-rebuild)
///   "1>------ Recompilação total iniciada: Projeto: Foo, Configuração: Debug x86 ------"
/// and the solution-level summary line
///   "========== Recompilar Tudo: 6 bem-sucedido, 1 falhou, 0 ignorado =========="
/// -- an English install's own wording (e.g. "Build started: Project:
/// Foo...") is expected to follow the same shape but hasn't itself been
/// confirmed live. The
/// regexes below match by *position* (first number = succeeded, second =
/// failed) rather than hardcoding PT-BR words, which should hold for other
/// UI languages using the same "========== &lt;verb&gt;: N succeeded, M
/// failed, ... ==========" shape MSBuild's console logger has used for
/// decades, though that hasn't itself been confirmed on a non-PT-BR VS.
///
/// One real caveat this technique can't avoid: the Output pane is a single
/// shared editor that swaps *which* channel's text it's showing (Build,
/// Debug, NuGet, ...) via its own "Show output from:" combo box -- while
/// the user has anything other than the Build channel selected, this reads
/// that other channel's text instead and simply sees no build lines at all,
/// silently, until they switch back. IsShowingBuildChannel below checks
/// the combo box's own current value and skips the poll rather than
/// misreading unrelated text when it isn't on Build.
///
/// A second, related caveat, deliberately NOT worked around: VS tears the
/// Output pane's own text element out of the accessibility tree entirely
/// once it's sat unselected (some other tool-window tab active, e.g. Lista
/// de Erros/Error List) for a while -- confirmed live it keeps updating fine
/// while *actively* selected, or while a build is actively running
/// regardless of which tab is selected, but goes stale once both a
/// different tab is active and things go idle. A first version reselected
/// the Output tab automatically whenever this happened; reported directly
/// that it kept yanking focus away from whatever else was on screen (VS
/// stealing a video player's focus mid-poll included), so this now just
/// silently does nothing on that poll instead -- detection only works while
/// the Output tab happens to be the active one, and that's an accepted
/// tradeoff, not a bug to keep chasing.
/// </summary>
public sealed class VisualStudioOutputMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    // "N>------ <verb> started: Project: <name>, Configuration: ... ------"
    // One line per project in a multi-project build; only the first one
    // seen since the last summary line is used to raise Started. Matched
    // positionally -- text up to the *second* colon, then up to the next
    // comma -- rather than hardcoding the word "Project"/"Projeto": an
    // earlier version literally matched the English word "Project:" and
    // silently never fired at all against this PT-BR VS install, whose real
    // line reads "1>------ Recompilação total iniciada: Projeto: Foo,
    // Configuração: ... ------" -- reported directly, confirmed by rereading
    // the exact text already captured during the live investigation.
    private static readonly Regex ProjectStartPattern =
        new(@"^\d+>-+\s*[^:]+:\s*[^:]+:\s*(.+?),", RegexOptions.Multiline | RegexOptions.Compiled);

    // "========== <verb>: N succeeded, M failed, K up-to-date/skipped =========="
    // Matched positionally (first number succeeded, second failed) so this
    // doesn't depend on the exact PT-BR/English wording, only MSBuild's
    // long-standing summary-line shape.
    private static readonly Regex SummaryPattern = new(
        @"^==========\s*.+?:\s*(\d+)\s+\S+,\s*(\d+)\s+\S+",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private sealed class TrackedInstance
    {
        public string LastText = "";
        // Whether a Started has fired that no Successful/Failed has matched
        // yet -- guards against re-firing Started for every remaining
        // project-start line in the same build, mirroring
        // GradleBuildLogMonitor's own per-build dedup.
        public bool AwaitingResult;
        public string? ProjectName;
    }

    private DispatcherTimer? _timer;
    private readonly Dictionary<int, TrackedInstance> _tracked = new();

    public event Action<BuildState, string?>? BuildStateChanged;
    public event Action<Exception>? Error;

    public void Start()
    {
        if (_timer != null)
        {
            return;
        }

        // Seed every currently-open VS instance's Output text as already
        // "seen" -- this monitor should only report builds that happen
        // *after* the checkbox is turned on, not replay history already
        // sitting in the pane.
        foreach (Process proc in FindDevenvProcesses())
        {
            AutomationElement? textEl = FindOutputTextElement(proc.MainWindowHandle);
            string text = textEl != null ? TryGetFullText(textEl) : "";
            _tracked[proc.Id] = new TrackedInstance { LastText = text };
        }

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _tracked.Clear();
    }

    public void Dispose() => Stop();

    private void Poll()
    {
        try
        {
            var seenPids = new HashSet<int>();
            foreach (Process proc in FindDevenvProcesses())
            {
                seenPids.Add(proc.Id);
                if (!_tracked.TryGetValue(proc.Id, out TrackedInstance? tracked))
                {
                    tracked = new TrackedInstance();
                    _tracked[proc.Id] = tracked;
                }

                PollInstance(proc, tracked);
            }

            foreach (int pid in _tracked.Keys.Where(p => !seenPids.Contains(p)).ToList())
            {
                _tracked.Remove(pid); // that VS window closed
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private void PollInstance(Process proc, TrackedInstance tracked)
    {
        AutomationElement? textEl = FindOutputTextElement(proc.MainWindowHandle);
        if (textEl == null)
        {
            return; // Output pane not currently realized -- see this class's own header comment
        }

        if (!IsShowingBuildChannel(textEl))
        {
            return; // see this class's own header comment
        }

        string text = TryGetFullText(textEl);
        if (text.Length == 0)
        {
            return;
        }

        string newText;
        if (text.Length >= tracked.LastText.Length && text.StartsWith(tracked.LastText, StringComparison.Ordinal))
        {
            newText = text[tracked.LastText.Length..];
        }
        else
        {
            // The pane was cleared ("Limpar Tudo") or swapped to a
            // different build's history -- nothing to safely diff against,
            // so just resync from here rather than misreading old content
            // as new.
            newText = "";
        }
        tracked.LastText = text;

        if (newText.Length == 0)
        {
            return;
        }

        if (!tracked.AwaitingResult)
        {
            Match startMatch = ProjectStartPattern.Match(newText);
            if (startMatch.Success)
            {
                tracked.AwaitingResult = true;
                tracked.ProjectName = startMatch.Groups[1].Value.Trim();
                BuildStateChanged?.Invoke(BuildState.Started, tracked.ProjectName);
            }
        }

        Match summaryMatch = SummaryPattern.Match(newText);
        if (summaryMatch.Success)
        {
            int failed = int.Parse(summaryMatch.Groups[2].Value);
            tracked.AwaitingResult = false;
            BuildStateChanged?.Invoke(failed > 0 ? BuildState.Failed : BuildState.Successful, tracked.ProjectName);
        }
    }

    /// <summary>
    /// The "Show output from:" combo box's own current selection -- best-
    /// effort: if this can't be read for any reason, this defaults to
    /// letting the poll proceed rather than silently going blind because of
    /// an automation quirk unrelated to which channel is actually selected.
    /// </summary>
    private static bool IsShowingBuildChannel(AutomationElement outputTextElement)
    {
        try
        {
            AutomationElement? root = TreeWalker.ControlViewWalker.GetParent(
                TreeWalker.ControlViewWalker.GetParent(
                    TreeWalker.ControlViewWalker.GetParent(outputTextElement)));
            if (root == null)
            {
                return true;
            }

            var comboCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox);
            AutomationElement? combo = root.FindFirst(TreeScope.Descendants, comboCond);
            if (combo == null || !combo.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj))
            {
                return true;
            }

            string selected = ((ValuePattern)patternObj).Current.Value ?? "";
            // "Compilação" (PT-BR) / "Build" (EN) -- checked as a substring
            // rather than an exact match since the combo's value can carry
            // extra decoration depending on VS version.
            return selected.Length == 0
                || selected.Contains("Compila", StringComparison.OrdinalIgnoreCase)
                || selected.Contains("Build", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static string TryGetFullText(AutomationElement textElement)
    {
        try
        {
            if (!textElement.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj))
            {
                return "";
            }
            return ((TextPattern)patternObj).DocumentRange.GetText(-1);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Walks from the "Output" toolbar (a stable, language-independent
    /// AutomationId-free anchor -- confirmed live its Name is the plain
    /// ASCII "Output" even on a PT-BR install) up to the tool pane's own
    /// container, then down to the WpfTextView inside it. Two ControlViewWalker
    /// hops up is what was confirmed live to land on the right container;
    /// a different VS version could in principle change that depth, which
    /// would just make this return null (Output pane "not found") rather
    /// than misreading some other pane's text.
    /// </summary>
    private static AutomationElement? FindOutputTextElement(IntPtr mainWindowHandle)
    {
        if (mainWindowHandle == IntPtr.Zero)
        {
            return null;
        }

        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(mainWindowHandle);
        }
        catch (Exception)
        {
            return null;
        }

        var toolbarCond = new PropertyCondition(AutomationElement.NameProperty, "Output");
        AutomationElement? toolbar = root.FindFirst(TreeScope.Descendants, toolbarCond);
        if (toolbar == null)
        {
            return null;
        }

        AutomationElement? container = TreeWalker.ControlViewWalker.GetParent(toolbar);
        container = container != null ? TreeWalker.ControlViewWalker.GetParent(container) : null;
        if (container == null)
        {
            return null;
        }

        var editCond = new PropertyCondition(AutomationElement.AutomationIdProperty, "WpfTextView");
        return container.FindFirst(TreeScope.Descendants, editCond);
    }

    private static IEnumerable<Process> FindDevenvProcesses()
    {
        return Process.GetProcessesByName("devenv");
    }
}
