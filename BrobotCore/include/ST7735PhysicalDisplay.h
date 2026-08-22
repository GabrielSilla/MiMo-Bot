#pragma once

#include "Config.h"
#include "IDisplay.h"
#include <Adafruit_GFX.h>
#include <Adafruit_ST7735.h>

// Drives a physical ST7735S over SPI via Adafruit's GFX/ST7735 libraries.
// Used when VSCREEN == 0.
//
// Draws accumulate into an in-RAM GFXcanvas16 framebuffer rather than
// hitting the panel directly — present() is the only point that touches
// SPI, pushing the whole finished frame at once instead of letting
// main.cpp's per-frame full-screen clear() flash visibly on the glass
// before the redraw lands on top of it.
//
// Confirmed working on an ESP32-C3 SuperMini + a 128x160 ST7735 clone:
// INITR_BLACKTAB, rotation 1. Don't change either without retesting on
// hardware — GREENTAB and rotation 3 were both tried and came out wrong
// (color and orientation respectively) on this exact panel.
class ST7735PhysicalDisplay : public IDisplay {
public:
    ST7735PhysicalDisplay(uint8_t csPin, uint8_t dcPin, uint8_t rstPin);

    void begin();

    int width() const override { return LOGICAL_WIDTH; }
    int height() const override { return LOGICAL_HEIGHT; }

    void clear(uint8_t r, uint8_t g, uint8_t b) override;
    void drawPixel(int x, int y, uint8_t r, uint8_t g, uint8_t b) override;
    void drawRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) override;
    void fillRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) override;
    void drawRoundedRect(int x, int y, int w, int h, int radius, uint8_t r, uint8_t g, uint8_t b) override;
    void drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b) override;
    void present() override;

private:
    Adafruit_ST7735 _tft;
    GFXcanvas16 _canvas;
};
