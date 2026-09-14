using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Brobot.VSExtension
{
    /// <summary>
    /// Reports a Visual Studio build starting/finishing to MiMo Sender --
    /// this is the safe replacement for the two external-automation
    /// approaches that were tried and rejected before it (see
    /// VisualStudioOutputMonitor.cs on the Sender side for the full story):
    /// plain DTE COM automation from an external process hung VS the moment
    /// a build started, and adding a proper IOleMessageFilter to that still
    /// hung it a second time. Both failures were specifically about an
    /// *external* STA client making cross-process/cross-apartment calls into
    /// VS's own UI thread. A package loaded in-process has none of that
    /// problem: DTE calls here are ordinary same-process, same-thread calls,
    /// the same way any of VS's own internal code already talks to itself.
    ///
    /// Deliberately does none of the UI-Automation Output-window scraping
    /// either -- that technique works, but only while the Output/"Saída" tab
    /// happens to be the selected tab in its dock group (VS tears the pane's
    /// own text element out of the accessibility tree once idle on a
    /// different tab), and the one fix that made it reliable regardless of
    /// tab (reselecting the tab on demand) was reported directly as visibly
    /// disruptive -- it kept stealing focus/tab selection from whatever else
    /// was on screen, video included. This package needs neither: it reads
    /// BuildEvents.OnBuildProjConfigBegin/Done directly (see below for why
    /// that pair specifically, over the solution-level OnBuildBegin/Done),
    /// the same authoritative source VisualStudioBuildMonitor's own earlier
    /// (reverted) DTE attempt used, just called safely from inside VS
    /// instead of from outside it.
    ///
    /// Talks to MiMo Sender over the exact same wire shape
    /// hooks/mimo-claude-hook.ps1 already uses for Claude Code events: one
    /// plain-text line, "EVENTNAME optional text...", to a fresh TCP
    /// connection on 127.0.0.1:5591 (AiThoughtsListener), then closes. Never
    /// throws out of an event handler and never blocks VS's UI thread on
    /// network I/O -- the socket write happens on a background thread, and
    /// any failure (MiMo Sender not running, no listener) is swallowed the
    /// same way the PowerShell hook script swallows its own failures: this
    /// must never be the thing that makes a Visual Studio build feel slow
    /// or broken.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [InstalledProductRegistration("MiMo Build Watcher", "Reports Visual Studio build start/finish to MiMo.", "1.0")]
    public sealed class BrobotBuildWatcherPackage : AsyncPackage
    {
        public const string PackageGuidString = "bbe2cf98-e9ab-481a-81a8-427b16c9c3bc";

        private const int AiThoughtsListenerPort = 5591;
        private const string EventStarted = "VsBuildStarted";
        private const string EventSucceeded = "VsBuildSucceeded";
        private const string EventFailed = "VsBuildFailed";

        private DTE? _dte;
        private BuildEvents? _buildEvents;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _dte = await GetServiceAsync(typeof(DTE)) as DTE;
            if (_dte == null)
            {
                return; // best-effort -- nothing to hook if the DTE service isn't available
            }

            // Kept as instance fields, not locals -- same reason
            // TeamsNotificationWatcher keeps its own WinEventDelegate alive:
            // a garbage-collected event source stops firing (or crashes)
            // with nothing left to root it.
            //
            // OnBuildProjConfigBegin/Done (per PROJECT being compiled), not
            // OnBuildBegin/OnBuildDone (per overall solution build) -- the
            // solution-level pair only tells you the build started/ended,
            // with no project name and no per-project result, so reporting
            // from it meant every message read as the *solution*'s name
            // ("NfePack") rather than whichever project was actually
            // compiling ("NFePack.Service.Core"), which is what MiMo's
            // screen is actually supposed to say. The per-project pair fires
            // once for each project MSBuild actually processes -- exactly
            // once for a single right-click "Build" on one project, several
            // times in sequence for a full solution build -- and hands back
            // the project's own name plus a direct success bool, so there's
            // no need for the solution-wide SolutionBuild.LastBuildInfo
            // guess the first version of this file used.
            _buildEvents = _dte.Events.BuildEvents;
            _buildEvents.OnBuildProjConfigBegin += OnBuildProjConfigBegin;
            _buildEvents.OnBuildProjConfigDone += OnBuildProjConfigDone;
        }

        private void OnBuildProjConfigBegin(string project, string projectConfig, string platform, string solutionConfig)
        {
            SendEvent(EventStarted, TryGetProjectDisplayName(project));
        }

        private void OnBuildProjConfigDone(string project, string projectConfig, string platform, string solutionConfig, bool success)
        {
            SendEvent(success ? EventSucceeded : EventFailed, TryGetProjectDisplayName(project));
        }

        /// <summary>
        /// EnvDTE's own "Project" parameter is the project's *unique* name,
        /// which for a project sitting inside a solution folder comes back
        /// as a relative path ("SolutionFolder\NFePack.Service.Core") rather
        /// than a bare name -- strip that down to just the leaf, same as
        /// TryGetSolutionName in the previous version of this file did for
        /// the solution's own path.
        /// </summary>
        private static string TryGetProjectDisplayName(string uniqueName)
        {
            try
            {
                string leaf = Path.GetFileName(uniqueName);
                return leaf.Length == 0 ? uniqueName : leaf;
            }
            catch (Exception)
            {
                return uniqueName;
            }
        }

        /// <summary>
        /// Fire-and-forget, deliberately not awaited from the build-event
        /// handlers above -- those run on VS's own UI thread, and a build
        /// notification blocking on a TCP connect/write (MiMo Sender could
        /// be slow to accept, or simply not running) must never be what
        /// makes Visual Studio itself feel sluggish. The discarded task
        /// (`_ = ...`) still runs to completion on its own, just without the
        /// caller waiting on or observing it.
        /// </summary>
        private static void SendEvent(string eventName, string? text)
        {
            _ = SendEventCoreAsync(eventName, text);
        }

        private static async Task SendEventCoreAsync(string eventName, string? text)
        {
            try
            {
                using var client = new TcpClient();
                Task connectTask = client.ConnectAsync("127.0.0.1", AiThoughtsListenerPort);
                if (await Task.WhenAny(connectTask, Task.Delay(1000)).ConfigureAwait(false) != connectTask)
                {
                    return; // MiMo Sender isn't listening -- nothing to do
                }
                await connectTask.ConfigureAwait(false); // observe a connect failure, if any

                using NetworkStream stream = client.GetStream();
                string line = string.IsNullOrEmpty(text) ? eventName : $"{eventName} {text}";
                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort, same as every other signal Brobot.Sender
                // reads from outside itself -- never worth surfacing an
                // error into the IDE over.
            }
        }
    }
}
