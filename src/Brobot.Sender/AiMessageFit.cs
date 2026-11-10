using System.Text;

namespace Brobot.Sender;

/// <summary>
/// Puts "Claude: " / "Codex: " in front of an AI activity message and makes
/// sure the whole thing still fits on Peemo's screen without scrolling.
///
/// Why "fits" has to mean "fits without scrolling" here, not just "under
/// Core's 255-byte capacity": both of Core's wrappers (Face.cpp's
/// drawWrappedMessage for the MSG balloon, drawNotificationText for NOTIFY)
/// show only the *last* three wrapped lines of a message. Anything longer
/// scrolls its first lines off the top as it types in — and the first line
/// is exactly where the sender's name is. A prefix that has scrolled away by
/// the time the message finishes typing tells nobody anything.
///
/// So this re-runs Core's own greedy word-wrap (same algorithm, same
/// widths, counted in UTF-8 bytes the way Core counts them, since an accent
/// is two bytes on the wire) and shortens only the *body* — never the
/// prefix — with three literal periods until it fits. The widths are
/// hand-mirrored from Face.cpp the same way MainWindow.CoreTypingCharIntervalMs
/// mirrors Face.h; nothing enforces the match, so a change to the screen
/// width or font advance on Core has to be repeated here.
///
/// The prefix is decided here, in the Sender, and never in the firmware:
/// Core only draws whatever MSG it is given (specs/overview.md's one rule).
/// </summary>
public static class AiMessageFit
{
    // Face.cpp: (160px - 2 * MESSAGE_MARGIN_X (8)) / CHAR_ADVANCE_PX (6) = 24,
    // MESSAGE_VISIBLE_LINES = 3. MATRIX's log line holds 74 bytes
    // (Face.h's MATRIX_LOG_LINE_CAPACITY), so anything that fits these
    // three 24-wide lines also fits there untruncated.
    public const int MessageCharsPerLine = 24;
    public const int MessageLines = 3;

    // Face.cpp: NOTIFICATION_TEXT_MAX_CHARS = 26, NOTIFICATION_TEXT_LINES = 3.
    public const int NotificationCharsPerLine = 26;
    public const int NotificationLines = 3;

    private const string Ellipsis = "...";

    /// <summary>
    /// "<paramref name="speaker"/>: <paramref name="body"/>", shortened so it
    /// wraps into at most <paramref name="lines"/> lines of
    /// <paramref name="charsPerLine"/>. A null/blank speaker means no prefix
    /// (the body is still fitted).
    /// </summary>
    public static string Fit(string? speaker, string body,
        int charsPerLine = MessageCharsPerLine, int lines = MessageLines)
    {
        string prefix = string.IsNullOrWhiteSpace(speaker) ? string.Empty : $"{speaker.Trim()}: ";
        body = body.Trim();

        string full = prefix + body;
        if (CountWrappedLines(full, charsPerLine) <= lines)
        {
            return full;
        }

        // Drop characters off the end of the body until prefix + body + "..."
        // fits, preferring to cut at a word boundary so the cut doesn't land
        // mid-word. Linear is fine: messages are at most a few hundred chars.
        string trimmedBody = body.EndsWith(Ellipsis, StringComparison.Ordinal)
            ? body[..^Ellipsis.Length].TrimEnd()
            : body;

        for (int length = trimmedBody.Length - 1; length > 0; length--)
        {
            string candidate = trimmedBody[..length];
            int lastSpace = candidate.LastIndexOf(' ');
            // Only snap back to a space when that still keeps most of the
            // candidate — one long word (a path, a URL) is better cut than
            // dropped entirely.
            if (lastSpace > length / 2)
            {
                candidate = candidate[..lastSpace];
            }

            string attempt = prefix + candidate.TrimEnd(' ', '.', ',', ':', ';', '-') + Ellipsis;
            if (CountWrappedLines(attempt, charsPerLine) <= lines)
            {
                return attempt;
            }
        }

        return (prefix + Ellipsis).Trim();
    }

    /// <summary>
    /// Face.cpp's greedy wrap, line count only: take up to
    /// <paramref name="charsPerLine"/> bytes; if that isn't the end of the
    /// text, back up to the last space inside the window (when there is one
    /// past position 0), never splitting an accent; then skip the spaces
    /// after the break.
    /// </summary>
    public static int CountWrappedLines(string text, int charsPerLine)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int len = bytes.Length;
        int pos = 0;
        int lineCount = 0;

        while (pos < len)
        {
            int remaining = len - pos;
            int take = remaining <= charsPerLine ? remaining : charsPerLine;

            if (take < remaining)
            {
                int breakAt = -1;
                for (int k = take - 1; k >= 0; k--)
                {
                    if (bytes[pos + k] == (byte)' ')
                    {
                        breakAt = k;
                        break;
                    }
                }
                if (breakAt > 0)
                {
                    take = breakAt;
                }
                // Face.cpp's utf8SafeTake: the only multi-byte glyphs Core
                // draws are the 0xC3-led Latin-1 accents, so a line that
                // would end on an 0xC3 lead byte gives that byte back to the
                // next line instead of splitting the accent in two.
                if (take > 0 && bytes[pos + take - 1] == 0xC3)
                {
                    take--;
                }
            }

            lineCount++;
            pos += take;
            while (pos < len && bytes[pos] == (byte)' ')
            {
                pos++;
            }
        }

        return lineCount;
    }
}
