using Brobot.Display.Abstractions;

namespace Brobot.Display.Simulator;

/// <summary>
/// Draws a fixed set of shapes, text and pixels purely to exercise every
/// <see cref="IDisplay"/> operation and validate that the renderer works.
///
/// This is a test pattern for the display itself. It does not represent
/// Brobot, its states, or its behavior in any way.
/// </summary>
public static class DisplayTestPattern
{
    public static void Render(IDisplay display, double timeSeconds)
    {
        display.Clear(DisplayColor.Black);

        int rectWidth = 20;
        int travel = display.Width - rectWidth;
        double phase = (Math.Sin(timeSeconds) + 1) / 2;
        int rectX = (int)(phase * travel);
        display.FillRect(rectX, 10, rectWidth, 14, DisplayColor.Red);

        display.DrawRect(10, 40, 40, 25, DisplayColor.Green);
        display.DrawRoundedRect(60, 40, 50, 25, 8, DisplayColor.Cyan);
        display.DrawRoundedRect(120, 30, 30, 30, 15, DisplayColor.Blue);

        for (int i = 0; i < 20; i++)
        {
            display.DrawPixel(10 + i, 80 + i, DisplayColor.Yellow);
        }

        display.DrawText("Hello World", 6, 110, DisplayColor.White);
    }
}
