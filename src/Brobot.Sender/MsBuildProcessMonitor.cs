using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Brobot.Sender;

/// <summary>
/// Detects an MSBuild-driven build: classic `MSBuild.exe`, `dotnet
/// build`/`dotnet msbuild` from a terminal, or a build launched *from*
/// Visual Studio -- VS's own "Build Solution" spawns exactly this kind of
/// child process, and this deliberately does NOT try to tell that case
/// apart from a terminal build (an earlier version excluded MSBuild/dotnet
/// processes parented by devenv.exe, on the assumption a separate
/// VisualStudioBuildMonitor would cover those with richer COM-automation
/// info instead; that monitor hung Visual Studio itself when a build
/// started and was removed, reported directly, so this is now the only
/// thing that sees a VS-launched build at all).
///
/// There is no ready-made success/failure signal for a bare command-line
/// build the way GradleBuildLogMonitor has an existing log file to read --
/// so this tracks the process itself: Started the moment a new matching
/// process appears, Successful/Failed read straight off its own exit code
/// once it's gone.
///
/// Reads that exit code via a raw Win32 handle (OpenProcess +
/// GetExitCodeProcess), not System.Diagnostics.Process.ExitCode -- confirmed
/// live, not assumed: Process.ExitCode throws
/// "Process was not started by this object, so requested information cannot
/// be determined" for a Process obtained via GetProcessById, *even though*
/// GetExitCodeProcess itself works fine on the exact same handle at the
/// Win32 level for any process this user owns. That's a managed-API-only
/// restriction, not an OS one, so bypassing it directly is what actually
/// makes this feature possible at all rather than only ever reporting "a
/// build ran".
///
/// `dotnet.exe` needs its command line actually read (via WMI -- the only
/// way to see another process's arguments; Process itself only exposes a
/// name and PID) since it's also the entry point for `dotnet run`, `dotnet
/// test`, `dotnet watch`, and this app's own dev-loop `dotnet build`
/// invocations from a terminal are exactly what should trigger this, so
/// there's no blanket exclusion for "any dotnet.exe". `MSBuild.exe` itself
/// needs no such check: that binary's only job is building.
/// </summary>
public sealed class MsBuildProcessMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr processHandle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;

    // Whole-token match ("build" or "msbuild" as their own argument, not a
    // substring) -- "dotnet build-server" is a real, unrelated subcommand,
    // and "dotnet rebuild" isn't a command dotnet has at all, but the
    // boundary check keeps this from false-matching what an actual "rebuild"
    // subcommand's name would otherwise look like.
    private static readonly Regex DotnetBuildVerb =
        new(@"(?<![\w-])(build|msbuild)(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The .sln/.slnx/.csproj/... path MSBuild/dotnet build was actually
    // pointed at, quoted or not -- e.g. `dotnet build "C:\...\Foo.sln"`.
    // MSBuild.exe run with no explicit target at all (it falls back to
    // whatever single project file sits in the current directory) simply
    // won't match this, and the message falls back to no project name.
    private static readonly Regex ProjectPathPattern = new(
        "\"([^\"]+\\.(?:sln|slnx|csproj|vbproj|fsproj))\"|(\\S+\\.(?:sln|slnx|csproj|vbproj|fsproj))\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private DispatcherTimer? _timer;
    private readonly Dictionary<int, (IntPtr Handle, string? ProjectName)> _trackedHandles = new();
    // Seeded once at Start() so already-running builds from before the
    // checkbox was turned on don't immediately count as "just started".
    private readonly HashSet<int> _seenAtStart = new();

    // Reused across polls rather than reconnected every 2s forever --
    // confirmed live: constructing a fresh ManagementObjectSearcher (and
    // therefore a fresh ManagementScope) on every single tick eventually hit
    // "Tentativa de operação ilegal em uma chave do Registro marcada para
    // exclusão" (COMException 0x800703FA) and then failed on every
    // subsequent poll forever -- a known WMI provider-host-recycling race,
    // not a transient blip. Set to null on any query failure so the next
    // poll reconnects from scratch instead of hammering the same broken
    // connection.
    private ManagementScope? _scope;

    public event Action<BuildState, string?>? BuildStateChanged;
    public event Action<Exception>? Error;

    public void Start()
    {
        if (_timer != null)
        {
            return;
        }

        foreach (var candidate in FindCandidates())
        {
            _seenAtStart.Add(candidate.Pid);
        }

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        foreach (var tracked in _trackedHandles.Values)
        {
            CloseHandle(tracked.Handle);
        }
        _trackedHandles.Clear();
        _seenAtStart.Clear();
        _scope = null;
    }

    public void Dispose() => Stop();

    private void Poll()
    {
        try
        {
            var currentPids = new HashSet<int>();
            foreach (var candidate in FindCandidates())
            {
                currentPids.Add(candidate.Pid);
                if (_seenAtStart.Contains(candidate.Pid) || _trackedHandles.ContainsKey(candidate.Pid))
                {
                    continue;
                }

                IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, candidate.Pid);
                if (handle == IntPtr.Zero)
                {
                    continue; // exited between the WMI query and here, or access denied
                }
                _trackedHandles[candidate.Pid] = (handle, candidate.ProjectName);
                BuildStateChanged?.Invoke(BuildState.Started, candidate.ProjectName);
            }
            _seenAtStart.IntersectWith(currentPids);

            foreach (int pid in _trackedHandles.Keys.ToList())
            {
                (IntPtr handle, string? projectName) = _trackedHandles[pid];
                if (!GetExitCodeProcess(handle, out uint exitCode))
                {
                    // The handle itself went bad -- best-effort, same as
                    // every other monitor's own external reads.
                    _trackedHandles.Remove(pid);
                    CloseHandle(handle);
                    continue;
                }
                if (exitCode == StillActive)
                {
                    continue; // still running
                }

                _trackedHandles.Remove(pid);
                CloseHandle(handle);
                BuildStateChanged?.Invoke(exitCode == 0 ? BuildState.Successful : BuildState.Failed, projectName);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private readonly record struct Candidate(int Pid, string? ProjectName);

    private IEnumerable<Candidate> FindCandidates()
    {
        var results = new List<Candidate>();

        if (_scope == null)
        {
            var scope = new ManagementScope("root\\cimv2");
            scope.Connect();
            _scope = scope;
        }

        ManagementObjectCollection queryResults;
        try
        {
            using var searcher = new ManagementObjectSearcher(_scope, new ObjectQuery(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process " +
                "WHERE Name = 'MSBuild.exe' OR Name = 'dotnet.exe'"));
            queryResults = searcher.Get();
        }
        catch (Exception)
        {
            // The connection itself went bad (see _scope's own comment) --
            // drop it so the next poll reconnects from scratch rather than
            // repeating the same failure forever.
            _scope = null;
            throw;
        }

        foreach (ManagementBaseObject item in queryResults)
        {
            using ManagementObject mo = (ManagementObject)item;
            int pid = (int)(uint)mo["ProcessId"];
            string name = (string)mo["Name"];
            string commandLine = mo["CommandLine"] as string ?? "";

            // Confirmed live: a single `dotnet build` spawns several extra
            // `dotnet.exe` worker/node processes (running MSBuild.dll with
            // "/nodemode:1"), and classic multi-proc MSBuild does the same
            // with plain MSBuild.exe -- without this, one real build fired
            // Started/Done once per worker instead of once total. /nodemode
            // is the one marker every one of them carries, regardless of
            // which host launched them.
            if (commandLine.Contains("/nodemode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(name, "dotnet.exe", StringComparison.OrdinalIgnoreCase)
                && !DotnetBuildVerb.IsMatch(commandLine))
            {
                continue;
            }

            results.Add(new Candidate(pid, ExtractProjectName(commandLine)));
        }
        return results;
    }

    private static string? ExtractProjectName(string commandLine)
    {
        Match match = ProjectPathPattern.Match(commandLine);
        if (!match.Success)
        {
            return null;
        }
        string path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        try
        {
            return Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
