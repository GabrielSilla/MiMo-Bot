namespace Brobot.Display.Abstractions;

/// <summary>
/// Which glyph set <see cref="IDisplay.DrawText"/> renders with. Mirrors
/// BrobotCore's <c>TextFont</c> (IDisplay.h) — see that file's comment for
/// why <see cref="Aurebesh"/> only actually applies to characters the
/// target display has a glyph for (this Simulator's own AurebeshFont only
/// covers A-Z/0-9, same as the physical build's AurebeshGFXFont).
/// </summary>
public enum TextFont
{
    Latin,
    Aurebesh,
}

/// <summary>
/// Represents the graphical capabilities of a display device, independent of
/// whatever backend renders it (a virtual simulator today, physical hardware
/// such as an ST7735S in the future).
///
/// Coordinates are always logical display coordinates: X in [0, Width),
/// Y in [0, Height). Implementations are responsible for translating these
/// into whatever their backend needs (screen pixels, hardware registers, etc.).
/// </summary>
public interface IDisplay
{
    /// <summary>Logical width of the display, in pixels.</summary>
    int Width { get; }

    /// <summary>Logical height of the display, in pixels.</summary>
    int Height { get; }

    /// <summary>Fills the entire display with a single color.</summary>
    void Clear(DisplayColor color);

    /// <summary>Sets a single pixel.</summary>
    void DrawPixel(int x, int y, DisplayColor color);

    /// <summary>Draws the outline of a rectangle.</summary>
    void DrawRect(int x, int y, int width, int height, DisplayColor color);

    /// <summary>Draws a filled rectangle.</summary>
    void FillRect(int x, int y, int width, int height, DisplayColor color);

    /// <summary>Draws the outline of a rectangle with rounded corners.</summary>
    void DrawRoundedRect(int x, int y, int width, int height, int radius, DisplayColor color);

    /// <summary>Draws text with its top-left corner at (x, y).</summary>
    void DrawText(string text, int x, int y, DisplayColor color, TextFont font = TextFont.Latin);

    /// <summary>
    /// Draws a bitmap image with its top-left corner at (x, y).
    /// <paramref name="pixels"/> is a row-major array of colors sized width * height.
    /// </summary>
    void DrawBitmap(int x, int y, int width, int height, DisplayColor[] pixels);
}
