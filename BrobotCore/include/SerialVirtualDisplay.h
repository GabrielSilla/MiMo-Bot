#pragma once

#include "Config.h"
#include "IDisplay.h"

// Sends every draw call as a protocol line over a Stream (Serial), to be
// rendered by the Brobot Virtual Display on the PC. See PROTOCOL.md.
// Used when VSCREEN == 1.
class SerialVirtualDisplay : public IDisplay {
public:
    explicit SerialVirtualDisplay(Stream& serial) : _serial(serial) {}

    int width() const override { return LOGICAL_WIDTH; }
    int height() const override { return LOGICAL_HEIGHT; }

    void clear(uint8_t r, uint8_t g, uint8_t b) override {
        _serial.print(F("CLR "));
        printColor(r, g, b);
    }

    void drawPixel(int x, int y, uint8_t r, uint8_t g, uint8_t b) override {
        _serial.print(F("PIXEL "));
        _serial.print(x); _serial.print(' '); _serial.print(y); _serial.print(' ');
        printColor(r, g, b);
    }

    void drawRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) override {
        printRectCommand(F("RECT "), x, y, w, h, r, g, b);
    }

    void fillRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) override {
        printRectCommand(F("FILLRECT "), x, y, w, h, r, g, b);
    }

    void drawRoundedRect(int x, int y, int w, int h, int radius, uint8_t r, uint8_t g, uint8_t b) override {
        _serial.print(F("RRECT "));
        _serial.print(x); _serial.print(' '); _serial.print(y); _serial.print(' ');
        _serial.print(w); _serial.print(' '); _serial.print(h); _serial.print(' ');
        _serial.print(radius); _serial.print(' ');
        printColor(r, g, b);
    }

    void drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b, TextFont font = TextFont::LATIN) override {
        _serial.print(F("TEXT "));
        _serial.print(x); _serial.print(' '); _serial.print(y); _serial.print(' ');
        _serial.print(r); _serial.print(' '); _serial.print(g); _serial.print(' '); _serial.print(b); _serial.print(' ');
        _serial.print(font == TextFont::AUREBESH ? F("AUREBESH") : F("LATIN")); _serial.print(' ');
        _serial.println(text);
    }

    void present() override {
        _serial.println(F("PRESENT"));
    }

private:
    Stream& _serial;

    void printColor(uint8_t r, uint8_t g, uint8_t b) {
        _serial.print(r); _serial.print(' '); _serial.print(g); _serial.print(' '); _serial.println(b);
    }

    void printRectCommand(const __FlashStringHelper* name, int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) {
        _serial.print(name);
        _serial.print(x); _serial.print(' '); _serial.print(y); _serial.print(' ');
        _serial.print(w); _serial.print(' '); _serial.print(h); _serial.print(' ');
        printColor(r, g, b);
    }
};
