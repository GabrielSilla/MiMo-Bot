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

    // Every event the bridge wires up, and the matcher each needs. Claude
    // Code requires "matcher" on some events and ignores it on others (see
    // the hook script's own comment) — an empty matcher is always valid to
    // include even where it isn't strictly required, so only PreToolUse and
    // Notification (which need to match "any tool"/"any notification") get
    // a real regex.
    private static readonly (string EventName, string Matcher)[] Events =
    {
        ("UserPromptSubmit", ""),
        ("PreToolUse", ".*"),
        ("Notification", ".*"),
        ("Stop", ""),
        ("SessionEnd", ""),
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    // The script ships as a copy-to-output file (see Brobot.Sender.csproj) so
    // it always sits next to whichever Brobot.Sender.exe is actually running,
    // rather than pointing at a hardcoded source-repo path.
    private static string HookScriptPath => Path.Combine(AppContext.BaseDirectory, HookScriptMarker);

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

        Save(root);
    }

    public static void Uninstall()
    {
        JsonObject? root = TryLoad();
        if (root?["hooks"] is not JsonObject hooks)
        {
            return;
        }

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

        Save(root);
    }

    private static string CommandFor(string eventName) =>
        $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{HookScriptPath}\" -EventName {eventName}";

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
