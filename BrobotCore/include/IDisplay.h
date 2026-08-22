#pragma once

#include <Arduino.h>

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
    virtual void drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b) = 0;

    // Marks the end of a frame: implementations that buffer output should
    // flush it here (e.g. send "PRESENT" over Serial).
    virtual void present() = 0;
};
