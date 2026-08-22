namespace Brobot.Display.Abstractions;

/// <summary>
/// Simple RGB888 color. ST7735S hardware works natively in RGB565,
/// so <see cref="ToRgb565"/> is provided for future hardware backends.
/// </summary>
public readonly struct DisplayColor
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public DisplayColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public static readonly DisplayColor Black = new(0, 0, 0);
    public static readonly DisplayColor White = new(255, 255, 255);
    public static readonly DisplayColor Red = new(255, 0, 0);
    public static readonly DisplayColor Green = new(0, 255, 0);
    public static readonly DisplayColor Blue = new(0, 0, 255);
    public static readonly DisplayColor Yellow = new(255, 255, 0);
    public static readonly DisplayColor Cyan = new(0, 255, 255);
    public static readonly DisplayColor Magenta = new(255, 0, 255);

    /// <summary>Packs this color into the 16-bit RGB565 format used by the ST7735S.</summary>
    public ushort ToRgb565()
    {
        int r5 = R >> 3;
        int g6 = G >> 2;
        int b5 = B >> 3;
        return (ushort)((r5 << 11) | (g6 << 5) | b5);
    }
}
