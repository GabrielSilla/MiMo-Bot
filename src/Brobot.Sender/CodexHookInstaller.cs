using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Brobot.Sender;

/// <summary>
/// Installs/removes the OpenAI Codex hooks that feed <see cref="AiThoughtsListener"/>
/// (see hooks/peemo-codex-hook.ps1) — the Codex counterpart of
/// <see cref="ClaudeCodeHookInstaller"/>, same shape and same rules.
///
/// Where: the user-level <c>hooks.json</c> in Codex's home
/// (<c>%CODEX_HOME%</c>, else <c>%USERPROFILE%\.codex</c>), so the bridge
/// applies to every project. Deliberately *not* <c>config.toml</c>: Codex
/// reads hooks from either, but config.toml is TOML full of the user's (and
/// the Codex app's own) settings — its single <c>notify</c> program is
/// already taken by the desktop app's computer-use helper on a typical
/// install — and there is no TOML library here to edit it surgically.
/// hooks.json is plain JSON with the same event → matcher group → handlers
/// shape Claude Code's settings.json uses, so it gets the same
/// System.Text.Json.Nodes treatment: every entry this class adds or removes
/// is identified by its "command" containing <see cref="HookScriptMarker"/>
/// plus "-EventName X", and anything else in the file is left alone.
///
/// Codex refuses to run a hook nobody has reviewed: trust is a hash per hook
/// that Codex itself records (in config.toml) once the user approves it in
/// the app (Settings → Hooks / "Review hooks") or the CLI (<c>/hooks</c>).
/// This class never writes that trust — skipping a security review on the
/// user's behalf isn't an installer's call — so after Instalar the hooks are
/// registered but stay inert until the user approves them once, which the
/// card's status text says.
/// </summary>
public static class CodexHookInstaller
{
    private const string HookScriptMarker = "peemo-codex-hook.ps1";

    // Codex's own event list, not Claude Code's (see the hook script's notes
    // for what exists). No matchers: an omitted matcher means "all", which is
    // what the tool-scoped ones want.
    //
    // Timeouts are explicit because Codex's default is 600s and it waits on
    // each hook (they run synchronously, which is also what keeps their order
    // — see AiThoughtsListener on why order matters). SessionEnd and
    // Interrupt are capped at 3s by Codex itself.
    //
    // Left out on purpose:
    // - PostToolUse: same reason as for Claude — PreToolUse already said it,
    //   and it would double the PowerShell processes per tool call. Codex has
    //   no failure-only variant (no PostToolUseFailure), so a failed Codex tool
    //   doesn't get FACE ERROR; a non-zero exit (grep finding nothing) isn't a
    //   failure anyway.
    // - PreModelRequest/PostModelRequest: they fire on every model request,
    //   about as often as tool calls, to say nothing the screen needs. (A
    //   failed PostModelRequest is the closest thing Codex has to Claude's
    //   StopFailure; not worth a process per request.)
    private static readonly (string EventName, int TimeoutSec)[] Events =
    {
        ("SessionStart", 5),
        ("UserPromptSubmit", 5),
        ("PreToolUse", 5),
        ("PermissionRequest", 5),
        ("SubagentStart", 5),
        ("SubagentStop", 5),
        ("PreCompact", 5),
        ("PostCompact", 5),
        ("Stop", 5),
        ("Interrupt", 3),
        ("SessionEnd", 3),
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Codex's home: CODEX_HOME when set (Codex honours it too), else ~/.codex.</summary>
    private static string CodexHome
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(env)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : env;
        }
    }

    private static string HooksPath => Path.Combine(CodexHome, "hooks.json");
    private static string ConfigTomlPath => Path.Combine(CodexHome, "config.toml");

    // Copy-to-output (see Brobot.Sender.csproj), so the hook points at the
    // script next to whichever Brobot.Sender.exe is actually running.
    private static string HookScriptPath => Path.Combine(AppContext.BaseDirectory, HookScriptMarker);

    // Absolute, not a bare "powershell": on Windows Codex runs the command
    // through cmd.exe /C with an environment rebuilt from its own session
    // snapshot, so nothing here should hinge on what PATH looks like there.
    private static string PowerShellPath => Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static bool IsInstalled()
    {
        JsonObject? root = TryLoad();
        if (root?["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        foreach ((string eventName, _) in Events)
        {
            if (!ContainsOurHook(hooks, eventName, requireCurrentCommand: true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when config.toml turns hooks off (<c>[features] hooks = false</c>,
    /// or the deprecated <c>codex_hooks</c> alias). Read-only and
    /// best-effort — a line scan, not a TOML parser — just so the card can say
    /// why nothing is arriving instead of looking installed and silent.
    /// </summary>
    public static bool HooksDisabledInConfig()
    {
        try
        {
            if (!File.Exists(ConfigTomlPath))
            {
                return false;
            }

            string? section = null;
            foreach (string rawLine in File.ReadLines(ConfigTomlPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith('['))
                {
                    section = line.Trim('[', ']', ' ');
                    continue;
                }

                if ((section == "features" && Regex.IsMatch(line, @"^(codex_)?hooks\s*=\s*false\b"))
                    || (section == null && Regex.IsMatch(line, @"^features\.(codex_)?hooks\s*=\s*false\b")))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    public static void Install()
    {
        JsonObject root = LoadForEdit() ?? new JsonObject();
        var hooks = root["hooks"] as JsonObject;
        if (hooks == null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach ((string eventName, int timeoutSec) in Events)
        {
            if (ContainsOurHook(hooks, eventName))
            {
                // Repair old quoting and stale build paths without duplicating
                // entries or changing somebody else's hooks/matcher groups.
                foreach (JsonNode? group in (JsonArray)hooks[eventName]!)
                {
                    if (group?["hooks"] is not JsonArray entries) continue;
                    foreach (JsonNode? entry in entries)
                    {
                        if (IsOurHookEntry(entry, eventName))
                        {
                            entry!["command"] = CommandFor(eventName);
                            entry["timeout"] = timeoutSec;
                        }
                    }
                }
                continue;
            }

            var matcherGroups = hooks[eventName] as JsonArray;
            if (matcherGroups == null)
            {
                matcherGroups = new JsonArray();
                hooks[eventName] = matcherGroups;
            }

            matcherGroups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = CommandFor(eventName),
                    ["timeout"] = timeoutSec,
                }),
            });
        }

        Save(root);
    }

    public static void Uninstall()
    {
        JsonObject? root = LoadForEdit();
        if (root == null)
        {
            return;
        }

        if (root["hooks"] is JsonObject hooks)
        {
            foreach ((string eventName, _) in Events)
            {
                if (hooks[eventName] is not JsonArray matcherGroups)
                {
                    continue;
                }

                for (int i = matcherGroups.Count - 1; i >= 0; i--)
                {
                    if (matcherGroups[i] is not JsonObject group || group["hooks"] is not JsonArray groupHooks)
                    {
                        continue;
                    }

                    for (int j = groupHooks.Count - 1; j >= 0; j--)
                    {
                        if (IsOurHookEntry(groupHooks[j], eventName))
                        {
                            groupHooks.RemoveAt(j);
                        }
                    }

                    // Only drop a matcher group once *we* emptied it.
                    if (groupHooks.Count == 0)
                    {
                        matcherGroups.RemoveAt(i);
                    }
                }

                if (matcherGroups.Count == 0)
                {
                    hooks.Remove(eventName);
                }
            }

            if (hooks.Count == 0)
            {
                root.Remove("hooks");
            }
        }

        // Nothing left at all means the file was only ever ours — remove it
        // rather than leave an empty "{}" behind in the user's Codex home.
        if (root.Count == 0)
        {
            File.Delete(HooksPath);
            return;
        }

        Save(root);
    }

    // Codex can use the session's PowerShell, not just cmd.exe. A quoted
    // executable is a string expression in PowerShell (and needs &), while
    // & is not portable to cmd. The normal Windows system path has no spaces,
    // so invoke it unquoted. On unusual space-containing Windows paths, use
    // the standard PowerShell launcher on PATH instead. The script stays quoted.
    private static string CommandFor(string eventName) =>
        $"{(PowerShellPath.Any(char.IsWhiteSpace) ? "powershell.exe" : PowerShellPath)} -NoProfile -ExecutionPolicy Bypass -File \"{HookScriptPath}\" -EventName {eventName}";

    private static bool ContainsOurHook(JsonObject hooks, string eventName, bool requireCurrentCommand = false)
    {
        if (hooks[eventName] is not JsonArray matcherGroups)
        {
            return false;
        }

        foreach (JsonNode? groupNode in matcherGroups)
        {
            if (groupNode is not JsonObject group || group["hooks"] is not JsonArray groupHooks)
            {
                continue;
            }

            foreach (JsonNode? hookNode in groupHooks)
            {
                if (IsOurHookEntry(hookNode, eventName)
                    && (!requireCurrentCommand || hookNode!["command"]!.GetValue<string>() == CommandFor(eventName)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Marker + event name rather than exact equality, so an install made from
    // another build/install location can still be found and removed. The
    // trailing boundary keeps "-EventName Stop" from matching "StopX".
    private static bool IsOurHookEntry(JsonNode? hookNode, string eventName) =>
        hookNode is JsonObject hookObj
        && hookObj["command"] is JsonValue commandValue
        && commandValue.TryGetValue(out string? cmd)
        && cmd.Contains(HookScriptMarker)
        && Regex.IsMatch(cmd, $@"-EventName {Regex.Escape(eventName)}(\s|""|$)");

    /// <summary>Lenient read for detection: anything unreadable just means "not installed".</summary>
    private static JsonObject? TryLoad()
    {
        try
        {
            return LoadForEdit();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Strict read for editing: a hooks.json that exists but can't be parsed
    /// throws instead of returning null, because null would make Install()
    /// start from an empty object and overwrite the user's own hooks with
    /// ours. Comments and trailing commas are tolerated.
    /// </summary>
    private static JsonObject? LoadForEdit()
    {
        if (!File.Exists(HooksPath))
        {
            return null;
        }

        string text = File.ReadAllText(HooksPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
                ?? throw new InvalidOperationException($"{HooksPath} não é um objeto JSON; corrija ou apague o arquivo antes de instalar.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{HooksPath} tem JSON inválido ({ex.Message}); nada foi alterado.", ex);
        }
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(CodexHome);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(HooksPath, root.ToJsonString(options));
    }
}
