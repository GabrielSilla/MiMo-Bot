using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Brobot.Sender;

/// <summary>
/// Detects a Gradle build starting/finishing by tailing the Gradle daemon's
/// own log files, not by watching any IDE -- confirmed live against a real
/// IntelliJ + Gradle build before writing this: every Gradle daemon writes
/// its full console output (the exact "BUILD SUCCESSFUL in 8s" text a
/// terminal would show) to
/// %USERPROFILE%\.gradle\daemon\&lt;gradleVersion&gt;\daemon-&lt;pid&gt;.out.log,
/// and a build's start is marked by a distinct
/// "Received command: Build{id=...}" line from
/// org.gradle.launcher.daemon.server.DefaultIncomingConnectionHandler,
/// confirmed to appear exactly once per build alongside exactly one
/// "BUILD SUCCESSFUL"/"BUILD FAILED" line.
///
/// This means zero configuration on the Gradle/IntelliJ side -- unlike the
/// Maven (EventSpy jar + MAVEN_OPTS) or JPS (ProjectTaskListener plugin)
/// alternatives that were also considered, the daemon already writes this
/// file on its own for every Gradle build regardless of what launched it
/// (IntelliJ, Android Studio, a bare `gradlew` in a terminal). The tradeoff
/// is scope: this only ever sees Gradle builds, never Maven or the IDE's
/// own native/JPS compiler -- there is no equivalent "already there" log
/// for either of those.
///
/// Polls on a DispatcherTimer rather than a FileSystemWatcher -- the first
/// version used a watcher and a real live build never triggered it even
/// once across three separate tries, matching this project's own
/// established distrust of OS-level change-notification APIs for exactly
/// this kind of external, high-frequency, third-party-process log file
/// (NotificationMonitor gave up on UserNotificationListener's own
/// NotificationChanged push event the same way, for the same reason: it
/// simply didn't fire reliably outside a packaged app, so it polls
/// GetNotificationsAsync on a timer instead). Polling re-lists the daemon
/// directory every tick, which is cheap (at most a handful of files) and
/// removes any doubt about whether a native callback is actually wired up.
/// </summary>
public sealed class GradleBuildLogMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly string DaemonLogRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle", "daemon");

    private const string BuildStartedMarker = "Received command: Build{";
    private const string BuildSuccessfulMarker = "BUILD SUCCESSFUL";
    private const string BuildFailedMarker = "BUILD FAILED";

    // The same "Received command: Build{id=..., currentDir=...}." line that
    // marks a build's start also carries the project directory it was run
    // from -- confirmed live, the exact line this class already tails for
    // its start marker. The folder name (not the full path) is what's worth
    // showing on a 160x128 screen.
    private static readonly Regex CurrentDirPattern =
        new(@"currentDir=([^,}]+)", RegexOptions.Compiled);

    private DispatcherTimer? _timer;

    // Per-file read offset, so each tick only tails the bytes actually
    // appended since last time instead of re-scanning the whole (often
    // multi-megabyte) log every 2s.
    private readonly Dictionary<string, long> _offsets = new();

    // Per-file, not a single shared field -- more than one daemon (and so
    // more than one project) can be tailed at once, and each one's own
    // BUILD SUCCESSFUL/FAILED line has to be paired with the project name
    // *that file's* most recent "Received command" line carried, not
    // whichever project happened to build last across every daemon.
    private readonly Dictionary<string, string?> _lastProjectNameByFile = new();

    public event Action<BuildState, string?>? BuildStateChanged;

    /// <summary>
    /// Surfaces a tailing failure so it shows up somewhere visible instead
    /// of vanishing into a swallowed catch block -- exactly what made the
    /// FileSystemWatcher version impossible to diagnose from outside.
    /// </summary>
    public event Action<Exception>? TailError;

    public void Start()
    {
        if (_timer != null)
        {
            return;
        }

        // Seed every existing log file to its current length -- this
        // monitor should only ever report builds that happen *after* the
        // checkbox is turned on, not replay a build from hours ago just
        // because its log file is still sitting there.
        if (Directory.Exists(DaemonLogRoot))
        {
            foreach (string path in Directory.EnumerateFiles(DaemonLogRoot, "*.out.log", SearchOption.AllDirectories))
            {
                _offsets[path] = SafeLength(path);
            }
        }

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _offsets.Clear();
        _lastProjectNameByFile.Clear();
    }

    public void Dispose() => Stop();

    private void Poll()
    {
        if (!Directory.Exists(DaemonLogRoot))
        {
            // No Gradle daemon has ever run under this user account yet --
            // nothing to watch. Not an error: the directory only appears
            // once a daemon actually starts.
            return;
        }

        foreach (string path in Directory.EnumerateFiles(DaemonLogRoot, "*.out.log", SearchOption.AllDirectories))
        {
            TailFile(path);
        }
    }

    private void TailFile(string path)
    {
        try
        {
            // FileShare.ReadWrite -- the Gradle daemon itself keeps this
            // file open for writing for as long as it's alive, so opening
            // it any less permissively would fail on every single tick.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            _offsets.TryGetValue(path, out long from);

            if (stream.Length < from)
            {
                // The daemon rotated/truncated its own log (or this is a
                // brand-new file reusing a stale offset) -- start over from
                // the top rather than seeking past the end.
                from = 0;
            }
            if (stream.Length == from)
            {
                return; // nothing new
            }

            stream.Seek(from, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                ProcessLine(path, line);
            }

            _offsets[path] = stream.Length;
        }
        catch (Exception ex)
        {
            // The file can vanish between EnumerateFiles and here (daemon
            // exiting, log rotation) -- best-effort telemetry, same as
            // every other monitor's own registry/window reads, but still
            // surfaced via TailError instead of disappearing silently.
            TailError?.Invoke(ex);
        }
    }

    private void ProcessLine(string path, string line)
    {
        if (line.Contains(BuildStartedMarker, StringComparison.Ordinal))
        {
            string? projectName = ExtractProjectName(line);
            _lastProjectNameByFile[path] = projectName;
            BuildStateChanged?.Invoke(BuildState.Started, projectName);
        }
        else if (line.Contains(BuildSuccessfulMarker, StringComparison.Ordinal))
        {
            _lastProjectNameByFile.TryGetValue(path, out string? projectName);
            BuildStateChanged?.Invoke(BuildState.Successful, projectName);
        }
        else if (line.Contains(BuildFailedMarker, StringComparison.Ordinal))
        {
            _lastProjectNameByFile.TryGetValue(path, out string? projectName);
            BuildStateChanged?.Invoke(BuildState.Failed, projectName);
        }
    }

    private static string? ExtractProjectName(string startLine)
    {
        Match match = CurrentDirPattern.Match(startLine);
        if (!match.Success)
        {
            return null;
        }
        try
        {
            return Path.GetFileName(match.Groups[1].Value.TrimEnd('\\', '/'));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
