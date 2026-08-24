#include "ST7735PhysicalDisplay.h"
#include <SPI.h>

// A cheap, integer-only take on the "Nostalgia CRT" style shader used on the
// XBSX2/PCSX2 side (see D:\MyProjects\XBSX2\IA.MD) — that one is a GPU
// fragment shader doing per-pixel trig/gaussian-blur/tube-curvature work,
// which the ESP32-C3 has no hope of running (RISC-V32IMC, no hardware FPU —
// every float op here is already software-emulated, same reason
// Personality.cpp hand-writes its own easing curves instead of calling
// sinf/powf). So instead of porting the shader, this ports the *look* with
// operations cheap enough for a bare MCU: shifts and adds, no per-pixel
// trig, no blur convolution.
//
// A geometric barrel/pincushion curvature warp (real corner distortion, not
// just darkening) was tried here too, ported from XBSX2's ng_curve in
// present.fx — but even tuned all the way down (three rounds: 70, 20, then
// 5 in Q8) it still read as too aggressive/distracting on a panel this
// small, and got pulled entirely rather than chase a fourth value. No
// vignette/corner effect at all now — just fringing/tint/scanline below.
namespace {

// Rolling scanline: one row in every SCANLINE_PERIOD_ROWS is dimmed, and
// which row that is steps down by one every SCANLINE_ROLL_INTERVAL_MS — so
// it reads as a tube slowly rolling/refreshing rather than a fixed dark grid
// burned into the frame. Dimming is deliberately light (see present(), -1/8
// not -1/2) — Font5x7 glyphs are only 7px tall, so a strong cut here was
// regularly slicing through letter strokes and hurting legibility; this is
// meant to read as texture, not as visible dark bars.
constexpr int SCANLINE_PERIOD_ROWS = 4;
constexpr unsigned long SCANLINE_ROLL_INTERVAL_MS = 90;

} // namespace

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
    // The CRT post-FX (fringing/tint/scanline) rides along in this same
    // per-row pass rather than a separate walk over the buffer, since every
    // row already gets touched here anyway.
    const uint16_t* buffer = _canvas.getBuffer();
    unsigned long nowMs = millis();
    int scanRollOffset = (int)((nowMs / SCANLINE_ROLL_INTERVAL_MS) % SCANLINE_PERIOD_ROWS);

    uint16_t outRow[LOGICAL_WIDTH];

    _tft.startWrite();
    _tft.setAddrWindow(0, 0, LOGICAL_WIDTH, LOGICAL_HEIGHT);
    for (int row = 0; row < LOGICAL_HEIGHT; row++) {
        const uint16_t* srcRow = buffer + row * LOGICAL_WIDTH;

        // SCANLINES off (see DeviceSettings/PROTOCOL.md): skip the whole
        // per-pixel post-FX pass and push the row as-is — also the cheaper
        // path CPU-wise, not just the plain-looking one.
        if (!_scanlinesEnabled) {
            _tft.writePixels(const_cast<uint16_t*>(srcRow), LOGICAL_WIDTH);
            continue;
        }

        bool isScanlineRow = ((row + scanRollOffset) % SCANLINE_PERIOD_ROWS) == 0;

        for (int col = 0; col < LOGICAL_WIDTH; col++) {
            // Chromatic fringing: red and blue are sampled from the
            // neighboring column instead of this one — a cheap stand-in for
            // the shader's per-channel offset sampling, no resampling math
            // needed since it's just an index shift.
            int rCol = col > 0 ? col - 1 : col;
            int bCol = col < LOGICAL_WIDTH - 1 ? col + 1 : col;
            uint8_t r5 = (srcRow[rCol] >> 11) & 0x1F;
            uint8_t g6 = (srcRow[col] >> 5) & 0x3F;
            uint8_t b5 = srcRow[bCol] & 0x1F;

            // Warm phosphor tint: nudge red up, blue down (~12.5% each via
            // shift, no multiply needed).
            r5 = (uint8_t)((r5 + (r5 >> 3)) > 31 ? 31 : (r5 + (r5 >> 3)));
            b5 = (uint8_t)(b5 - (b5 >> 3));

            if (isScanlineRow) {
                r5 = (uint8_t)(r5 - (r5 >> 3));
                g6 = (uint8_t)(g6 - (g6 >> 3));
                b5 = (uint8_t)(b5 - (b5 >> 3));
            }

            outRow[col] = (uint16_t)((r5 << 11) | (g6 << 5) | b5);
        }

        _tft.writePixels(outRow, LOGICAL_WIDTH);
    }
    _tft.endWrite();
}
