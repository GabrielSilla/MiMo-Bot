using System.Text;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// Cleanup and checks for text that comes from the internet (translated
/// facts, "neste dia" events) rather than being written against
/// specs/voice-guide.md: typographic punctuation is folded into what
/// Font5x7 / the physical display can draw, and anything still not
/// drawable is rejected rather than shown as garbage.
/// </summary>
internal static class ThoughtText
{
    /// <summary>Longest fetched text accepted — a little over the ~75 authored phrases aim for, since the message box scrolls.</summary>
    public const int MaxFetchedLength = 90;

    // Mirrors Font5x7.Chars (Brobot.Display.Simulator) — the physical
    // display draws the same set. Uppercase accented letters aren't in it
    // but are fine: both displays fall back to the bare letter.
    private const string DrawableChars =
        " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,!?'-:abcdefghijklmnopqrstuvwxyzãáàâéêíóôõúç>[]/%_#()";
    private const string UppercaseAccented = "ÃÁÀÂÉÊÍÓÔÕÚÇ";

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Trim())
        {
            switch (c)
            {
                case '“': case '”': case '„': case '"': case '‘': case '’': case '`': case '´':
                    sb.Append('\''); break;
                case '–': case '—': case '−':
                    sb.Append('-'); break;
                case '…':
                    sb.Append("..."); break;
                case ';':
                    sb.Append(','); break;
                case 'ü':
                    sb.Append('u'); break;
                case ' ': case '\t': case '\n': case '\r':
                    sb.Append(' '); break;
                default:
                    sb.Append(c); break;
            }
        }
        // Collapse the runs of spaces the replacements above can leave.
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " {2,}", " ");
    }

    public static bool IsDrawable(string text) =>
        text.All(c => DrawableChars.Contains(c) || UppercaseAccented.Contains(c));

    /// <summary>Normalized text if it's drawable and short enough, otherwise null.</summary>
    public static string? Accept(string text)
    {
        string clean = Normalize(text);
        return clean.Length > 0 && clean.Length <= MaxFetchedLength && IsDrawable(clean) ? clean : null;
    }
}
