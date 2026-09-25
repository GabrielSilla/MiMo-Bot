# WPF app internals (Brobot.Display.Simulator)

- **`Font5x7.cs`**: hand-built 5x7 bitmap font (space, A-Z, a-z, 0-9, basic
  punctuation, `:`, lowercase Portuguese diacritics — ã á à â é ê í ó ô õ ú ç,
  and terminal punctuation `> [ ] / % _ # ( )`),
  rendered pixel-by-pixel — not the OS font rasterizer, which was illegible at
  this size. `CharAdvance` (6px) must match `CHAR_ADVANCE_PX` in `Face.cpp` or
  word-wrap breaks in the wrong place. If you ever need to add glyphs, generate
  the packed bytes with a script that round-trips ASCII-art → bytes → ASCII-art
  and diffs against the original — don't hand-encode bitmap bytes, it's
  error-prone (this is exactly how `:` and the diacritics got added — and how a
  real bug got caught: the original `g` glyph's descender was a single stray
  pixel, which read as a cut-off tail rather than a hook, fixed by widening it
  to 2px; the terminal punctuation went in the same way, for PEEMO84). Adding
  those last ones also fixed a bug that predated PEEMO84 and only ever showed up
  here: MATRIX's tab header and log prefix already used `>` `[` `]`, and the
  stats rows already used `%`, none of which this font had — `GetGlyph`
  returned `null`, which draws nothing but still advances the cursor, so on
  the Simulator they were silently rendering as blank gaps. They had always
  looked right on the physical ST7735, which uses Adafruit_GFX's built-in
  full-ASCII font, which is why the discrepancy went unnoticed. `GetGlyph` returning `null` for an unmapped character silently draws
  nothing but still advances the cursor (a missing glyph is a gap, not a crash
  or a shifted string) — `GetGlyph` now also falls back to the plain base
  letter for uppercase accented characters with no dedicated glyph (e.g. `Ã` →
  `A`, via `StripDiacritic`), since lowercase letters have rows 0-1 free for an
  accent mark but uppercase letters already use the full 7-row cell.
- **`SerialDisplayBridge.cs`**: now just interprets `BrobotConnection`'s frames as
  `SimulatorDisplay` calls (`ProcessLine`) — the actual connection is
  `Brobot.Connection/BrobotConnection.cs` (see [connection.md](connection.md)).
- **`MainWindow`**: scale selector includes 1x–8x. Default scale is 3x, sized for
  the 160x128 landscape frame — a too-large scale can make the display overflow
  the fixed-size window (it doesn't auto-resize).
- **`DisplayTestPattern.cs`**: local demo shapes only, unrelated to Brobot — exists
  purely to exercise the renderer.
