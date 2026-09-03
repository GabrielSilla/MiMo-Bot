using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Brobot.Sender;

/// <summary>Who is currently signed in to Claude Code.</summary>
public sealed record ClaudeAccount(string Uuid, string DisplayName);

/// <summary>
/// Reads the signed-in Claude Code account out of the user's own
/// <c>%USERPROFILE%\.claude.json</c>.
///
/// This exists because **switching accounts has no hook event**. It was worth
/// checking rather than assuming: measured against the real desktop app with
/// every hook this bridge installs, an account switch produced no
/// <c>Notification</c> at all — only a burst of <c>SessionEnd</c>/
/// <c>SessionStart</c>, indistinguishable from any other session restart. The
/// documented <c>auth_success</c> notification type never arrived, so an
/// earlier attempt to detect the switch from the hook payload was removed
/// rather than left in as speculative dead code.
///
/// What does change is this file, so MainWindow compares the account it finds
/// here against the one it last saw (<see cref="SenderSettings.LastClaudeAccountUuid"/>)
/// on every SessionStart. The comparison is on <c>accountUuid</c>, not on the
/// name: two accounts can share a display name, and a display name can be
/// edited without the account changing.
///
/// Read-only and best-effort by design — this is another application's config
/// file, so a missing file, a schema change or a half-written file all just
/// mean "no account information", never an error. Note this is
/// <c>.claude.json</c> in the home directory, a *different* file from the
/// <c>.claude\settings.json</c> that <see cref="ClaudeCodeHookInstaller"/>
/// writes; nothing here ever writes to either.
/// </summary>
public static class ClaudeCodeAccount
{
    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    public static ClaudeAccount? TryRead()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(ConfigPath)) is not JsonObject root)
            {
                return null;
            }

            if (root["oauthAccount"] is not JsonObject account)
            {
                return null;
            }

            string? uuid = Text(account, "accountUuid");
            if (string.IsNullOrWhiteSpace(uuid))
            {
                return null; // nothing stable to compare against — better than guessing at a switch
            }

            // displayName is what Claude Code itself shows; fullName and the
            // email address are fallbacks for an account that hasn't got one.
            // The email is last on purpose: it's the least pleasant thing to
            // read off a desk display, not least because it's the longest.
            string name = Text(account, "displayName")
                ?? Text(account, "fullName")
                ?? Text(account, "emailAddress")
                ?? "outra conta";

            return new ClaudeAccount(uuid, name);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject obj, string key)
    {
        string? value = obj[key]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
