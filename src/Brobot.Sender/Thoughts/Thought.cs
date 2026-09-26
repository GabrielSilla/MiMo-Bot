namespace Brobot.Sender.Thoughts;

/// <summary>
/// One thing Peemo "thinks out loud" (see specs/sender-thoughts.md). Face
/// is a Core expression token; SATELLITE/SPACE make it a space thought,
/// which goes out as a full-screen NOTIFY instead of the usual FACE + MSG.
/// PhraseId (when the source has stable ids) feeds ThoughtHistoryStore cooldowns.
/// </summary>
internal sealed record Thought(string Face, string Text, string Source, string? Category = null, string? PhraseId = null)
{
    public bool IsSpace => Face is "SATELLITE" or "SPACE";
}
