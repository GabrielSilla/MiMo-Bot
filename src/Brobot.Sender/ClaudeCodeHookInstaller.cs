using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Brobot.Sender;

/// <summary>
/// Installs/removes the Claude Code hooks that feed <see cref="AiThoughtsListener"/>
/// (see hooks/mimo-claude-hook.ps1), by editing the user's global Claude Code
/// settings — %USERPROFILE%\.claude\settings.json — so the bridge applies to
/// every project, not just whichever one happens to be open.
///
/// Edits are surgical, not a wholesale rewrite: every hook entry this class
/// ever adds or removes is identified by its "command" containing
/// <see cref="HookScriptMarker"/> plus the specific event name, so any other
/// hooks the user already has configured (their own, or another tool's) are
/// left completely alone. Detection (<see cref="IsInstalled"/>) reads the
/// file fresh each time rather than trusting cached app state, so it stays
/// correct even if the user hand-edits settings.json or reinstalls Claude
/// Code between launches of MiMo.
/// </summary>
public static class ClaudeCodeHookInstaller
{
    private const string HookScriptMarker = "mimo-claude-hook.ps1";
    private const string StatusLineScriptMarker = "mimo-claude-statusline.ps1";

    // Every event the bridge wires up, and the matcher each needs. Claude
    // Code requires "matcher" on some events and ignores it on others (see
    // the hook script's own comment) — an empty matcher is always valid to
    // include even where it isn't strictly required, so only PreToolUse and
    // Notification (which need to match "any tool"/"any notification") get
    // a real regex.
    // Tool-scoped events (PreToolUse and the permission/failure ones) match
    // "any tool"; everything else needs no matcher. Adding an event here is
    // half of wiring one up — the other half is an arm in
    // hooks/mimo-claude-hook.ps1 (to extract its text) and one in
    // MainWindow.OnAiThoughtReceived (to decide what MiMo does about it).
    //
    // What's deliberately absent is as considered as what's here.
    // PostToolUse (success) is left out because PreToolUse already announced
    // that same call and Core's own FACE_OVERRIDE_DURATION_MS already returns
    // the face on its own afterwards — installing it would double the
    // PowerShell processes spawned per tool call to say nothing new. So are
    // MessageDisplay, FileChanged, InstructionsLoaded, ConfigChange,
    // DirectoryAdded and TeammateIdle, which fire often enough that MiMo
    // would strobe rather than report. Only PostToolUseFailure is taken from
    // that family, because a tool *failing* is genuinely new information and
    // is the only thing in the whole bridge that can legitimately show ERROR.
    private static readonly (string EventName, string Matcher)[] Events =
    {
        ("SessionStart", ""),
        ("UserPromptSubmit", ""),
        ("PreToolUse", ".*"),
        ("PostToolUseFailure", ".*"),
        ("PermissionRequest", ".*"),
        ("PermissionDenied", ".*"),
        ("Notification", ".*"),
        ("SubagentStart", ""),
        ("SubagentStop", ""),
        ("PreCompact", ""),
        ("PostCompact", ""),
        ("Stop", ""),
        ("StopFailure", ""),
        ("PreModelSwitch", ""),
        ("PostModelSwitch", ""),
        ("TaskCreated", ""),
        ("TaskCompleted", ""),
        ("CwdChanged", ""),
        ("SessionEnd", ""),
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    // The script ships as a copy-to-output file (see Brobot.Sender.csproj) so
    // it always sits next to whichever Brobot.Sender.exe is actually running,
    // rather than pointing at a hardcoded source-repo path.
    private static string HookScriptPath => Path.Combine(AppContext.BaseDirectory, HookScriptMarker);
    private static string StatusLineScriptPath => Path.Combine(AppContext.BaseDirectory, StatusLineScriptMarker);

    public static bool IsInstalled()
    {
        JsonObject? root = TryLoad();
        if (root?["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        foreach ((string eventName, _) in Events)
        {
            if (!ContainsOurHook(hooks, eventName))
            {
                return false;
            }
        }

        return true;
    }

    public static void Install()
    {
        JsonObject root = TryLoad() ?? new JsonObject();
        var hooks = root["hooks"] as JsonObject;
        if (hooks == null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach ((string eventName, string matcher) in Events)
        {
            if (ContainsOurHook(hooks, eventName))
            {
                continue; // already there (or a previous partial install) — don't duplicate
            }

            var matcherGroups = hooks[eventName] as JsonArray;
            if (matcherGroups == null)
            {
                matcherGroups = new JsonArray();
                hooks[eventName] = matcherGroups;
            }

            var hookEntry = new JsonObject
            {
                ["type"] = "command",
                ["command"] = CommandFor(eventName),
                ["timeout"] = 5,
            };

            var group = new JsonObject();
            if (!string.IsNullOrEmpty(matcher))
            {
                group["matcher"] = matcher;
            }
            group["hooks"] = new JsonArray(hookEntry);

            matcherGroups.Add(group);
        }

        // statusLine is a single top-level object, not an array of
        // independently-installable entries like hooks — only claim it when
        // it's empty or already ours, so a statusLine the user configured
        // themselves (their own script, another tool's) is never clobbered.
        // Best-effort: IsInstalled() below still only checks hooks, so a
        // conflict here doesn't leave the "Instalar" button permanently
        // unable to report success.
        if (root["statusLine"] is not JsonObject || IsOurStatusLine(root))
        {
            root["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = StatusLineCommand,
            };
        }

        Save(root);
    }

    public static void Uninstall()
    {
        JsonObject? root = TryLoad();
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

                    // Only drop the matcher group once *we* emptied it — a group
                    // that still has the user's own other hooks in it stays.
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

        if (IsOurStatusLine(root))
        {
            root.Remove("statusLine");
        }

        Save(root);
    }

    private static string CommandFor(string eventName) =>
        $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{HookScriptPath}\" -EventName {eventName}";

    private static string StatusLineCommand =>
        $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{StatusLineScriptPath}\"";

    private static bool IsOurStatusLine(JsonObject? root) =>
        root?["statusLine"] is JsonObject statusLine
        && statusLine["command"]?.GetValue<string>() is string cmd
        && cmd.Contains(StatusLineScriptMarker);

    private static bool ContainsOurHook(JsonObject hooks, string eventName)
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
                if (IsOurHookEntry(hookNode, eventName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Matches on the marker + event name rather than exact command equality,
    // so an install made from a different build/install location than the
    // one currently running can still be found and cleanly removed.
    private static bool IsOurHookEntry(JsonNode? hookNode, string eventName) =>
        hookNode is JsonObject hookObj
        && hookObj["command"]?.GetValue<string>() is string cmd
        && cmd.Contains(HookScriptMarker)
        && cmd.Contains($"-EventName {eventName}");

    private static JsonObject? TryLoad()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Save(JsonObject root)
    {
        string? dir = Path.GetDirectoryName(SettingsPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsPath, root.ToJsonString(options));
    }
}
