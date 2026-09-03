#include "ST7735PhysicalDisplay.h"
#include "AurebeshGFXFont.h"
#include "LatinAccentGFXFont.h"
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

namespace {
// AurebeshGFXFont only covers '0'-'9' and 'A'-'Z' (see its own header
// comment) — anything else (space, punctuation, lowercase/accented PT-BR
// text) falls back to the built-in font even when the caller asked for
// AUREBESH, same "missing glyph degrades gracefully" convention the
// Simulator's own AurebeshFont.GetGlyph fallback uses.
bool isAurebeshCovered(char c) {
    char upper = (char)toupper((unsigned char)c);
    return (upper >= '0' && upper <= '9') || (upper >= 'A' && upper <= 'Z');
}

// Every accented Latin-1 letter this app ever sends — from a Claude Code
// hook's real prose, not just this codebase's own (deliberately
// accent-free, for exactly this reason) hardcoded PT-BR strings — arrives
// on the wire as UTF-8: two bytes, lead byte 0xC3 followed by a
// continuation byte 0x80-0xBF. The classic built-in Adafruit font has no
// idea about any of that; drawing each byte separately drew two wrong
// glyphs instead of one accented letter, which is the whole bug this
// decoder exists to fix. `0xC0 + (continuation & 0x3F)` recovers the exact
// Latin-1 codepoint for the *entire* C0-FF block this way — uppercase and
// lowercase alike — since UTF-8's 2-byte form for U+0080-U+07FF always
// encodes a Latin-1 Supplement codepoint (U+00C0-U+00FF) with this same
// lead byte. Returns the decoded codepoint and advances `p` past however
// many bytes it consumed (2 for a recognized sequence, 1 otherwise) — a
// lone/dangling 0xC3 with no valid continuation byte (e.g. a message cut
// off by the typewriter effect mid-character) falls through to "just this
// one raw byte", which duplicates the exact one-byte-at-a-time behavior
// this function is replacing for anything it doesn't specifically handle.
uint8_t decodeNextCodepoint(const char*& p) {
    uint8_t lead = (uint8_t)*p;
    if (lead == 0xC3) {
        uint8_t cont = (uint8_t)p[1];
        if (cont >= 0x80 && cont <= 0xBF) {
            p += 2;
            return (uint8_t)(0xC0 + (cont & 0x3F));
        }
    }
    p += 1;
    return lead;
}

// LatinAccentGFXFont's own populated codepoints (see its header) — the rest
// of its 0xE0-0xFA range is blank placeholder glyphs, present only so the
// range stays contiguous.
bool isLatinAccentCovered(uint8_t codepoint) {
    switch (codepoint) {
        case 0xE0: case 0xE1: case 0xE2: case 0xE3: case 0xE7: case 0xE9:
        case 0xEA: case 0xED: case 0xF3: case 0xF4: case 0xF5: case 0xFA:
            return true;
        default:
            return false;
    }
}

// An UPPERCASE accented letter (sentence-initial "Não", say) has no glyph
// of its own — same reasoning Font5x7.cs's own StripDiacritic already
// settled on: a lowercase letter has rows 0-1 free above it for the mark,
// but an uppercase one already fills the whole 7-row cell, leaving nowhere
// to put one. Falls back to the plain base letter (still readable, unlike
// a dropped glyph) via the classic built-in font; returns '\0' for
// anything this table doesn't cover (ñ, ü, ...), which the caller takes as
// "draw nothing" rather than risk two more wrong classic-font glyphs.
char stripLatinAccent(uint8_t codepoint) {
    switch (codepoint) {
        case 0xC1: case 0xC0: case 0xC2: case 0xC3: return 'A'; // Á À Â Ã
        case 0xC9: case 0xCA: return 'E';                       // É Ê
        case 0xCD: return 'I';                                  // Í
        case 0xD3: case 0xD4: case 0xD5: return 'O';            // Ó Ô Õ
        case 0xDA: return 'U';                                  // Ú
        case 0xC7: return 'C';                                  // Ç
        default: return '\0';
    }
}
}  // namespace

void ST7735PhysicalDisplay::drawText(const char* text, int x, int y, uint8_t r, uint8_t g, uint8_t b, TextFont font) {
    _canvas.setTextColor(_tft.color565(r, g, b));
    _canvas.setTextWrap(false);

    // Drawn one character at a time (rather than one _canvas.print(text)
    // call) so a run can freely mix AUREBESH-covered, accented-Latin, and
    // plain characters, switching Adafruit_GFX's active font per glyph.
    // Advances by a flat 6px regardless of which font drew it or how many
    // *wire bytes* a character consumed — AurebeshGFXGlyph/LatinAccentGFXGlyph
    // both set xAdvance 6, and the built-in font already advances 6px/char
    // at textSize 1 (5px glyph + 1px gap), matching CHAR_ADVANCE_PX (see
    // Face.cpp) so MI2MO2's per-character word-wrap math stays correct on
    // this display too — an accented letter is still exactly one column
    // wide on screen even though it took two bytes on the wire.
    int penX = x;
    for (const char* p = text; *p != '\0'; ) {
        const char* charStart = p;
        uint8_t codepoint = decodeNextCodepoint(p);
        bool consumedTwoBytes = (p - charStart) == 2;

        if (consumedTwoBytes) {
            if (isLatinAccentCovered(codepoint)) {
                // Same baseline-vs-top-left cursor adjustment Aurebesh
                // already needs (see below) — LatinAccentGFXFont uses the
                // identical yOffset=-7 convention.
                _canvas.setFont(&LatinAccentGFXFont);
                _canvas.setCursor(penX, y + 7);
                _canvas.write(codepoint);
            } else if (char base = stripLatinAccent(codepoint)) {
                _canvas.setFont(nullptr);
                _canvas.setCursor(penX, y);
                _canvas.write((uint8_t)base);
            }
            // Anything else (ñ, ü, ...): silently draw nothing rather than
            // the two wrong classic-font glyphs this whole function exists
            // to stop drawing.
        } else if (font == TextFont::AUREBESH && isAurebeshCovered((char)codepoint)) {
            // A custom GFXfont draws relative to the text *baseline*, unlike
            // the built-in font's top-left cursor (which every other
            // drawText call in this codebase assumes) — shift the cursor
            // down by the glyph's full height (7px, baked into the font's
            // yOffset=-7) so the glyph's top still lands on the caller's own
            // top-left `y`, keeping it level with any LATIN characters
            // drawn right before/after it in the same message.
            _canvas.setFont(&AurebeshGFXFont);
            _canvas.setCursor(penX, y + 7);
            _canvas.write((uint8_t)toupper(codepoint));
        } else {
            _canvas.setFont(nullptr);
            _canvas.setCursor(penX, y);
            _canvas.write(codepoint);
        }
        penX += 6;
    }
    _canvas.setFont(nullptr);  // leave the canvas in its normal default-font state for every other drawText caller
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
