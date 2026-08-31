using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brobot.Display.Abstractions;

namespace Brobot.Display.Simulator;

/// <summary>
/// WPF-backed implementation of <see cref="IDisplay"/>.
///
/// Owns a 160x128 logical framebuffer in plain memory. Draw operations only
/// mutate that framebuffer; nothing is pushed to the screen until
/// <see cref="Present"/> is called, which uploads the whole buffer to a
/// <see cref="WriteableBitmap"/> in a single operation. This keeps draw-call
/// frequency decoupled from bitmap/GPU upload frequency.
///
/// This class only renders. It has no knowledge of what it is drawing or why.
/// </summary>
public sealed class SimulatorDisplay : IDisplay
{
    public const int LogicalWidth = 160;
    public const int LogicalHeight = 128;

    private readonly DisplayColor[] _framebuffer = new DisplayColor[LogicalWidth * LogicalHeight];
    private readonly byte[] _uploadBuffer = new byte[LogicalWidth * LogicalHeight * 4];
    private readonly WriteableBitmap _bitmap;

    public SimulatorDisplay()
    {
        _bitmap = new WriteableBitmap(LogicalWidth, LogicalHeight, 96, 96, PixelFormats.Bgr32, null);
    }

    public int Width => LogicalWidth;
    public int Height => LogicalHeight;

    /// <summary>The bitmap that visually represents the current framebuffer. Bind this to an Image control.</summary>
    public WriteableBitmap Bitmap => _bitmap;

    /// <summary>Number of draw operations issued since the display was created. Reset with <see cref="ResetDrawOperationCount"/>.</summary>
    public int DrawOperationCount { get; private set; }

    /// <summary>Number of times <see cref="Present"/> has been called. Lets callers measure FPS regardless of what drives rendering.</summary>
    public int PresentCount { get; private set; }

    public void ResetDrawOperationCount() => DrawOperationCount = 0;

    public void Clear(DisplayColor color)
    {
        Array.Fill(_framebuffer, color);
        DrawOperationCount++;
    }

    public void DrawPixel(int x, int y, DisplayColor color)
    {
        SetPixel(x, y, color);
        DrawOperationCount++;
    }

    public void DrawRect(int x, int y, int width, int height, DisplayColor color)
    {
        if (width <= 0 || height <= 0)
        {
            DrawOperationCount++;
            return;
        }

        int right = x + width - 1;
        int bottom = y + height - 1;

        for (int px = x; px <= right; px++)
        {
            SetPixel(px, y, color);
            SetPixel(px, bottom, color);
        }

        for (int py = y; py <= bottom; py++)
        {
            SetPixel(x, py, color);
            SetPixel(right, py, color);
        }

        DrawOperationCount++;
    }

    public void FillRect(int x, int y, int width, int height, DisplayColor color)
    {
        for (int py = y; py < y + height; py++)
        {
            for (int px = x; px < x + width; px++)
            {
                SetPixel(px, py, color);
            }
        }

        DrawOperationCount++;
    }

    public void DrawRoundedRect(int x, int y, int width, int height, int radius, DisplayColor color)
    {
        if (width <= 0 || height <= 0)
        {
            DrawOperationCount++;
            return;
        }

        int outerRadius = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
        int innerRadius = Math.Max(0, outerRadius - 1);

        for (int py = y; py < y + height; py++)
        {
            for (int px = x; px < x + width; px++)
            {
                bool outer = IsInsideRoundedRect(px, py, x, y, width, height, outerRadius);
                bool inner = IsInsideRoundedRect(px, py, x + 1, y + 1, Math.Max(width - 2, 0), Math.Max(height - 2, 0), innerRadius);
                if (outer && !inner)
                {
                    SetPixel(px, py, color);
                }
            }
        }

        DrawOperationCount++;
    }

    public void DrawText(string text, int x, int y, DisplayColor color, TextFont font = TextFont.Latin)
    {
        int penX = x;

        foreach (char c in text)
        {
            // AurebeshFont only covers A-Z/0-9 (see its own comment) — falls
            // back to the normal Latin glyph for anything it doesn't map
            // (space, punctuation, lowercase), same as Font5x7's own
            // missing-glyph convention.
            byte[]? glyph = font == TextFont.Aurebesh ? AurebeshFont.GetGlyph(c) ?? Font5x7.GetGlyph(c) : Font5x7.GetGlyph(c);
            if (glyph != null)
            {
                for (int col = 0; col < Font5x7.GlyphWidth; col++)
                {
                    byte columnBits = glyph[col];
                    for (int row = 0; row < Font5x7.GlyphHeight; row++)
                    {
                        if ((columnBits & (1 << row)) != 0)
                        {
                            SetPixel(penX + col, y + row, color);
                        }
                    }
                }
            }

            penX += Font5x7.CharAdvance;
        }

        DrawOperationCount++;
    }

    public void DrawBitmap(int x, int y, int width, int height, DisplayColor[] pixels)
    {
        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                SetPixel(x + px, y + py, pixels[py * width + px]);
            }
        }

        DrawOperationCount++;
    }

    /// <summary>Uploads the current framebuffer contents to the WPF bitmap. Call once per rendered frame.</summary>
    public void Present()
    {
        for (int i = 0; i < _framebuffer.Length; i++)
        {
            DisplayColor color = _framebuffer[i];
            int offset = i * 4;
            _uploadBuffer[offset] = color.B;
            _uploadBuffer[offset + 1] = color.G;
            _uploadBuffer[offset + 2] = color.R;
            _uploadBuffer[offset + 3] = 255;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, LogicalWidth, LogicalHeight), _uploadBuffer, LogicalWidth * 4, 0);
        PresentCount++;
    }

    private void SetPixel(int x, int y, DisplayColor color)
    {
        if ((uint)x >= (uint)LogicalWidth || (uint)y >= (uint)LogicalHeight)
        {
            return;
        }

        _framebuffer[y * LogicalWidth + x] = color;
    }

    private static bool IsInsideRoundedRect(int px, int py, int rx, int ry, int rw, int rh, int radius)
    {
        if (rw <= 0 || rh <= 0 || px < rx || py < ry || px >= rx + rw || py >= ry + rh)
        {
            return false;
        }

        bool left = px < rx + radius;
        bool right = px >= rx + rw - radius;
        bool top = py < ry + radius;
        bool bottom = py >= ry + rh - radius;

        if ((left || right) && (top || bottom) && radius > 0)
        {
            double cx = left ? rx + radius - 0.5 : rx + rw - radius - 0.5;
            double cy = top ? ry + radius - 0.5 : ry + rh - radius - 0.5;
            double dx = px - cx;
            double dy = py - cy;
            return dx * dx + dy * dy <= (double)radius * radius;
        }

        return true;
    }
}
