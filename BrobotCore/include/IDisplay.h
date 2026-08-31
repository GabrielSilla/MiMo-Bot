#pragma once

#include <Arduino.h>

// Which glyph set drawText renders with. AUREBESH is only actually honored
// for characters the target display has an Aurebesh glyph for — '0'-'9'/
// 'A'-'Z' on both the Brobot Virtual Display (Font5x7 vs AurebeshFont, a
// 5x7 bitmap font) and the physical ST7735 build (AurebeshGFXFont, an
// Adafruit_GFX custom font at the same 5x7 size) — anything else (space,
// punctuation, lowercase/accented PT-BR text) falls back to LATIN
// regardless of what's requested, same "missing glyph degrades gracefully"
// convention as a missing Font5x7 glyph. Passed as an extra token on the
// wire protocol's TEXT line (see PROTOCOL.md) so Core can drive it same as
// any other draw call.
enum class TextFont : uint8_t { LATIN, AUREBESH };

// Graphical capabilities of a display device. Mirrors the IDisplay
// abstraction on the Brobot Virtual Display (C#) project: same operations,
// same 128x160 logical coordinate space. Implementations only render —
// they never decide what to draw.
class IDisplay {
public:
    virtual ~IDisplay() {}

    virtual int width() const = 0;
    virtual int height() const = 0;

    virtual void clear(uint8_t r, uint8_t g, uint8_t b) = 0;
    virtual void drawPixel(int x, int y, uint8_t r, uint8_t g, uint8_t b) = 0;
    virtual void drawRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) = 0;
    virtual void fillRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) = 0;
    virtual void drawRoundedRect(int x, int y, int w, int h, int radius, uint8_t r, uint8_t g, uint8_t b) = 0;
    virtual void drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b, TextFont font = TextFont::LATIN) = 0;

    // Marks the end of a frame: implementations that buffer output should
    // flush it here (e.g. send "PRESENT" over Serial).
    virtual void present() = 0;
};
