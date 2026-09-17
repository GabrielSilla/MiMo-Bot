using System;
using System.Diagnostics;
using System.IO;

namespace Brobot.Sender;

/// <summary>
/// Installs/removes the global git hook that feeds <see cref="AiThoughtsListener"/>
/// (see hooks/mimo-git-hook.ps1 and the shim scripts under hooks/git-hooks),
/// by pointing git's own <c>core.hooksPath</c> — a single global config
/// value, not per-repo — at the folder those shims get copied to next to
/// whichever Brobot.Sender.exe is actually running. Machine-wide by design,
/// same reasoning ClaudeCodeHookInstaller edits the user's *global*
/// settings.json rather than one project's: it should fire for every git
/// repo on the machine, not just whichever one happens to be open.
///
/// Started/stopped from "Ferramentas de Dev"'s checkbox
/// (MainWindow.BuildCheckBox_CheckedChanged) alongside the Gradle/MSBuild
/// monitors, not from its own button the way Atividade da IA's install is —
/// there's no separate "provider" choice to make here the way Claude/Codex/
/// Gemini/Cursor is, so a checkbox is the right shape.
/// </summary>
public static class GitHookInstaller
{
    // Copied to output alongside mimo-git-hook.ps1 (see hooks/git-hooks and
    // the csproj) — the shim scripts (post-commit/post-merge/post-checkout/
    // pre-push) are static and never rewritten by this class; all it ever
    // touches is core.hooksPath itself, pointed at this one folder.
    private static string HooksDir => Path.Combine(AppContext.BaseDirectory, "git-hooks");

    public static bool IsInstalled()
    {
        string? current = ReadHooksPath();
        return current != null && PathsMatch(current, HooksDir);
    }

    /// <summary>
    /// Never overwrites a hooksPath the user already had configured — same
    /// "claim only if empty or already ours" caution ClaudeCodeHookInstaller
    /// applies to Claude Code's own statusLine entry, just for a config
    /// scalar instead of a JSON object: unlike settings.json's per-event hook
    /// arrays, core.hooksPath is a single global value with no room for two
    /// owners to coexist, so a pre-existing custom one is left alone entirely
    /// rather than merged or replaced.
    /// </summary>
    public static void Install()
    {
        string? current = ReadHooksPath();
        if (current != null && !PathsMatch(current, HooksDir))
        {
            return;
        }

        RunGit($"config --global core.hooksPath \"{Normalize(HooksDir)}\"");
    }

    /// <summary>Only unsets it if it's still ours — never clears a value something else set later.</summary>
    public static void Uninstall()
    {
        string? current = ReadHooksPath();
        if (current != null && PathsMatch(current, HooksDir))
        {
            RunGit("config --global --unset core.hooksPath");
        }
    }

    private static string? ReadHooksPath()
    {
        string? output = RunGit("config --global --get core.hooksPath");
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    private static bool PathsMatch(string a, string b) =>
        string.Equals(Normalize(a).TrimEnd('/'), Normalize(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // git config always reads/writes hooksPath with forward slashes regardless
    // of platform; comparing both sides normalized avoids a false "different
    // path" mismatch against our own Windows-style AppContext.BaseDirectory.
    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Swallows everything — git not being on PATH, the process failing to
    /// start, "--get" exiting non-zero because nothing is configured yet —
    /// same best-effort treatment every other optional watcher in this app
    /// gets; a missing git install must never crash the checkbox. Returns
    /// stdout, or null on any failure.
    /// </summary>
    private static string? RunGit(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch
        {
            return null;
        }
    }
}
