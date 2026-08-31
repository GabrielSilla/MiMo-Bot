namespace Brobot.Display.Simulator;

/// <summary>
/// A 5x7 pixel font rendering A-Z/0-9 as the Star Wars "Aurebesh" script,
/// used by MI2MO2's typing effect (see <see cref="SimulatorDisplay.DrawText"/>
/// and <c>Face.cpp</c>'s <c>drawWrappedMessageMi2Mo2</c>): a message's
/// characters are drawn in this font right after being revealed, then swap
/// to <see cref="Font5x7"/> a moment later — reads as the message
/// "translating" from alien script into Portuguese in real time.
///
/// Same 5x7/column-major/CharAdvance=6 grid as <see cref="Font5x7"/> (the
/// firmware's word-wrap math assumes a fixed 6px advance regardless of which
/// font is active, so this can't use a different cell size). Glyphs were
/// generated, not hand-drawn: a script rendered each letter from the actual
/// Aurebesh.otf font at high resolution, then reduced each 5x7 cell to the
/// *darkest* pixel in its source block (not a smooth resize — a plain resize
/// anti-aliases thin diagonal strokes, like the digit '7', away to nothing
/// before the threshold step ever sees them). No case distinction — Aurebesh
/// itself has none, so <see cref="AurebeshFont.GetGlyph"/> only maps
/// uppercase; <see cref="SimulatorDisplay.DrawText"/> falls back to
/// <see cref="Font5x7"/> for anything this font doesn't cover (lowercase,
/// space, punctuation), same "missing glyph isn't a crash" convention
/// Font5x7 itself already uses.
/// </summary>
internal static class AurebeshFont
{
    public const int GlyphWidth = Font5x7.GlyphWidth;
    public const int GlyphHeight = Font5x7.GlyphHeight;
    public const int CharAdvance = Font5x7.CharAdvance;

    private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static readonly byte[][] Glyphs =
    {
        new byte[] {0x7E, 0x3C, 0x1C, 0x1C, 0x7E}, // 'A'
        new byte[] {0x7E, 0x7E, 0x7E, 0x7E, 0x36}, // 'B'
        new byte[] {0x78, 0x78, 0x18, 0x1E, 0x00}, // 'C'
        new byte[] {0x0E, 0x3E, 0x7E, 0x7E, 0x06}, // 'D'
        new byte[] {0x7E, 0x1E, 0x7C, 0x7E, 0x0E}, // 'E'
        new byte[] {0x6C, 0x7E, 0x7E, 0x78, 0x78}, // 'F'
        new byte[] {0x3E, 0x7E, 0x66, 0x66, 0x7E}, // 'G'
        new byte[] {0x7E, 0x7E, 0x7E, 0x7E, 0x66}, // 'H'
        new byte[] {0x00, 0x7E, 0x7E, 0x04, 0x00}, // 'I'
        new byte[] {0x1E, 0x7C, 0x78, 0x78, 0x78}, // 'J'
        new byte[] {0x7E, 0x66, 0x66, 0x66, 0x76}, // 'K'
        new byte[] {0x7E, 0x30, 0x38, 0x18, 0x08}, // 'L'
        new byte[] {0x66, 0x66, 0x7E, 0x78, 0x60}, // 'M'
        new byte[] {0x7C, 0x1E, 0x7E, 0x76, 0x7E}, // 'N'
        new byte[] {0x7E, 0x6E, 0x66, 0x7E, 0x7C}, // 'O'
        new byte[] {0x7E, 0x60, 0x66, 0x7E, 0x3E}, // 'P'
        new byte[] {0x06, 0x06, 0x66, 0x66, 0x7E}, // 'Q'
        new byte[] {0x0E, 0x3E, 0x7E, 0x66, 0x06}, // 'R'
        new byte[] {0x7E, 0x38, 0x7C, 0x3E, 0x36}, // 'S'
        new byte[] {0x38, 0x7E, 0x7E, 0x38, 0x18}, // 'T'
        new byte[] {0x7E, 0x66, 0x66, 0x60, 0x7E}, // 'U'
        new byte[] {0x06, 0x7C, 0x7C, 0x0E, 0x06}, // 'V'
        new byte[] {0x66, 0x66, 0x66, 0x66, 0x7E}, // 'W'
        new byte[] {0x78, 0x7E, 0x6E, 0x7C, 0x70}, // 'X'
        new byte[] {0x1E, 0x7C, 0x7E, 0x3E, 0x0E}, // 'Y'
        new byte[] {0x7E, 0x64, 0x64, 0x6C, 0x78}, // 'Z'
        new byte[] {0x7E, 0x7E, 0x7E, 0x7E, 0x7E}, // '0'
        new byte[] {0x66, 0x7E, 0x7E, 0x60, 0x60}, // '1'
        new byte[] {0x76, 0x76, 0x76, 0x7E, 0x7E}, // '2'
        new byte[] {0x7E, 0x7E, 0x7E, 0x7E, 0x3E}, // '3'
        new byte[] {0x1E, 0x18, 0x18, 0x7E, 0x7E}, // '4'
        new byte[] {0x7E, 0x6E, 0x6E, 0x7E, 0x3E}, // '5'
        new byte[] {0x7E, 0x6E, 0x6E, 0x7E, 0x3E}, // '6'
        new byte[] {0x06, 0x06, 0x06, 0x7E, 0x7E}, // '7'
        new byte[] {0x7E, 0x7E, 0x7E, 0x7E, 0x7E}, // '8'
        new byte[] {0x7E, 0x76, 0x76, 0x7E, 0x7E}, // '9'
    };

    public static byte[]? GetGlyph(char c)
    {
        char upper = char.ToUpperInvariant(c);
        int index = Chars.IndexOf(upper);
        return index >= 0 ? Glyphs[index] : null;
    }
}
