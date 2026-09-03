#pragma once

#include <Adafruit_GFX.h>
#include <Arduino.h>

// Adafruit_GFX custom font for the Brazilian-Portuguese Latin-1 diacritics
// on the physical display — the classic built-in Adafruit font it draws
// everything else with has no accented glyphs at all, so a UTF-8 letter
// like "ã" (2 bytes on the wire) used to draw as two wrong classic-font
// glyphs, one per byte, instead of an "ã". Firmware-only, same as
// AurebeshGFXFont.h — Adafruit_GFX isn't part of the native dev build, so
// this header is only ever included from ST7735PhysicalDisplay.cpp.
//
// Covers exactly the lowercase diacritics Font5x7.cs already supports on
// the Simulator side (á ã à â é ê í ó ô õ ú ç) — uppercase accented
// letters aren't in here at all, matching that side's own StripDiacritic
// fallback (uppercase falls back to the plain base letter rather than
// getting its own glyph, since there's no free row left to fit a mark
// above a full-height capital in a 7-row cell); ST7735PhysicalDisplay's
// own drawText mirrors that same fallback table for the same reason.
// Codepoints run 0xE0-0xFA (Latin-1 Supplement — exactly what a UTF-8
// 2-byte sequence with lead byte 0xC3 decodes to) as a single contiguous
// GFXfont range, with blank placeholder glyphs filling the gaps between
// the twelve real ones — the same trick AurebeshGFXFont uses for its own
// punctuation gap, since a GFXfont can't express two disjoint ranges.
//
// Generated, not hand-drawn: each glyph was rasterized from a real .ttf
// (Type Machine) at high resolution with anti-aliasing on, then reduced by
// averaging (not "any dark pixel wins", which oversaturated a diacritic
// mark squeezed into just 2 rows) into this 5x7 cell — rows 0-1 the accent
// mark, rows 2-6 the letter body, the same split the classic built-in
// font's own lowercase letters already use (confirmed by decoding its own
// glyph bytes for 'a': rows 0-1 there are blank too). 'í' and 'ú' reuse the
// classic font's own stem shapes for the letter body instead of the source
// font's — both are narrow enough that the automated rasterize/reduce step
// that worked cleanly for the other ten produced an unrecognizable stem
// for these two specifically. 'ç' is hand-drawn rather than rasterized at
// all: the rows0-1-accent/rows2-6-body split this pipeline assumes doesn't
// hold for a cedilla, whose mark hangs *below* the letter rather than above
// it, so running 'ç' through the same automated step produced an
// unrecognizable near-closed box instead of a 'c'. Its glyph instead
// shrinks the 'c' loop to rows 2-5 (mirroring Font5x7.cs's own fix, and
// this font's existing 'g'-style 2px-hook convention) to free row 6 for the
// cedilla hook. Packed the same row-major/MSB-first way
// AurebeshGFXFont already is, and round-tripped (unpacked back to ASCII
// art and diffed against the source) before being pasted in here, to catch
// a packing bug in the generator rather than on the real display.
//
// Each glyph's yOffset is -7 (the full glyph height), same reasoning as
// AurebeshGFXFont: a GFXfont draws relative to the text baseline, not a
// top-left corner like every other IDisplay draw call in this codebase, so
// ST7735PhysicalDisplay::drawText shifts its cursor y down by that same
// 7px before switching to this font.
const uint8_t LatinAccentGFXBitmaps[] PROGMEM = {
    0xF1, 0xF8, 0x2F, 0x4B, 0xE0, 0x7F, 0x38, 0x2F, 0x4B, 0xE0, 0x74, 0x78, 0x2F, 0x4B, 0xE0, 0xFC,
    0xB8, 0x2F, 0x4B, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x1D, 0x08, 0x38, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7F, 0x1D, 0x1F,
    0xC5, 0xE0, 0x74, 0x5D, 0x1F, 0xC5, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x7F, 0x18, 0x42, 0x11, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7F,
    0x1D, 0x18, 0xC5, 0xC0, 0x74, 0x5D, 0x18, 0xC5, 0xC0, 0xFC, 0x9D, 0x18, 0xC5, 0xC0, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x7F, 0x23, 0x18, 0xCD, 0xA0,
};

const GFXglyph LatinAccentGFXGlyphs[] PROGMEM = {
    {0, 5, 7, 6, 0, -7},   // 'à' (0xE0)
    {5, 5, 7, 6, 0, -7},   // 'á' (0xE1)
    {10, 5, 7, 6, 0, -7},  // 'â' (0xE2)
    {15, 5, 7, 6, 0, -7},  // 'ã' (0xE3)
    {20, 5, 7, 6, 0, -7},  // (0xE4) — blank placeholder, never drawn
    {25, 5, 7, 6, 0, -7},  // (0xE5) — blank placeholder, never drawn
    {30, 5, 7, 6, 0, -7},  // (0xE6) — blank placeholder, never drawn
    {35, 5, 7, 6, 0, -7},  // 'ç' (0xE7)
    {40, 5, 7, 6, 0, -7},  // (0xE8) — blank placeholder, never drawn
    {45, 5, 7, 6, 0, -7},  // 'é' (0xE9)
    {50, 5, 7, 6, 0, -7},  // 'ê' (0xEA)
    {55, 5, 7, 6, 0, -7},  // (0xEB) — blank placeholder, never drawn
    {60, 5, 7, 6, 0, -7},  // (0xEC) — blank placeholder, never drawn
    {65, 5, 7, 6, 0, -7},  // 'í' (0xED)
    {70, 5, 7, 6, 0, -7},  // (0xEE) — blank placeholder, never drawn
    {75, 5, 7, 6, 0, -7},  // (0xEF) — blank placeholder, never drawn
    {80, 5, 7, 6, 0, -7},  // (0xF0) — blank placeholder, never drawn
    {85, 5, 7, 6, 0, -7},  // (0xF1) — blank placeholder, never drawn
    {90, 5, 7, 6, 0, -7},  // (0xF2) — blank placeholder, never drawn
    {95, 5, 7, 6, 0, -7},  // 'ó' (0xF3)
    {100, 5, 7, 6, 0, -7}, // 'ô' (0xF4)
    {105, 5, 7, 6, 0, -7}, // 'õ' (0xF5)
    {110, 5, 7, 6, 0, -7}, // (0xF6) — blank placeholder, never drawn
    {115, 5, 7, 6, 0, -7}, // (0xF7) — blank placeholder, never drawn
    {120, 5, 7, 6, 0, -7}, // (0xF8) — blank placeholder, never drawn
    {125, 5, 7, 6, 0, -7}, // (0xF9) — blank placeholder, never drawn
    {130, 5, 7, 6, 0, -7}, // 'ú' (0xFA)
};

const GFXfont LatinAccentGFXFont PROGMEM = {
    (uint8_t*)LatinAccentGFXBitmaps, (GFXglyph*)LatinAccentGFXGlyphs,
    0xE0, 0xFA, 9
};
