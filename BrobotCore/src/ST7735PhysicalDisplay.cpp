#include "ST7735PhysicalDisplay.h"
#include <SPI.h>

ST7735PhysicalDisplay::ST7735PhysicalDisplay(uint8_t csPin, uint8_t dcPin, uint8_t rstPin)
    : _tft(csPin, dcPin, rstPin), _canvas(LOGICAL_WIDTH, LOGICAL_HEIGHT) {
}

void ST7735PhysicalDisplay::begin() {
    // Board-default hardware SPI pins aren't reliable across ESP32-C3 clone
    // boards (the SuperMini's don't match some board profiles' assumed
    // defaults), so SCK/MOSI are pinned explicitly rather than left to
    // Adafruit_ST7735's implicit SPI.begin(). MISO (-1) is unused — the
    // ST7735S is write-only over this link.
    SPI.begin(TFT_SCK_PIN, -1, TFT_MOSI_PIN, TFT_CS_PIN);
    _tft.initR(INITR_BLACKTAB);
    _tft.setRotation(1); // landscape: matches LOGICAL_WIDTH/HEIGHT (160x128) in Config.h
    _tft.fillScreen(ST77XX_BLACK);
}

void ST7735PhysicalDisplay::clear(uint8_t r, uint8_t g, uint8_t b) {
    _canvas.fillScreen(_tft.color565(r, g, b));
}

void ST7735PhysicalDisplay::drawPixel(int x, int y, uint8_t r, uint8_t g, uint8_t b) {
    _canvas.drawPixel(x, y, _tft.color565(r, g, b));
}

void ST7735PhysicalDisplay::drawRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) {
    _canvas.drawRect(x, y, w, h, _tft.color565(r, g, b));
}

void ST7735PhysicalDisplay::fillRect(int x, int y, int w, int h, uint8_t r, uint8_t g, uint8_t b) {
    _canvas.fillRect(x, y, w, h, _tft.color565(r, g, b));
}

void ST7735PhysicalDisplay::drawRoundedRect(int x, int y, int w, int h, int radius, uint8_t r, uint8_t g, uint8_t b) {
    _canvas.drawRoundRect(x, y, w, h, radius, _tft.color565(r, g, b));
}

void ST7735PhysicalDisplay::drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b) {
    _canvas.setTextColor(_tft.color565(r, g, b));
    _canvas.setTextWrap(false);
    _canvas.setCursor(x, y);
    _canvas.print(text);
}

void ST7735PhysicalDisplay::present() {
    // The only point that touches SPI. Written in small per-scanline
    // chunks (not one big ~40KB burst) — a single bulk writePixels() call
    // this size came out sheared into parallelograms on this hardware
    // (looked like an ESP32 SPI/DMA issue with very large transfers), even
    // though it's still just one addressed window / one startWrite session.
    uint16_t* buffer = _canvas.getBuffer();
    _tft.startWrite();
    _tft.setAddrWindow(0, 0, LOGICAL_WIDTH, LOGICAL_HEIGHT);
    for (int row = 0; row < LOGICAL_HEIGHT; row++) {
        _tft.writePixels(buffer + row * LOGICAL_WIDTH, LOGICAL_WIDTH);
    }
    _tft.endWrite();
}
