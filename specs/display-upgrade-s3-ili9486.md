# Display upgrade: ESP32-S3 + ILI9486 3.5" (PLANNED, not implemented)

> Status: design only, nothing built. Written from a feasibility discussion
> on 2026-09-30; every number marked **(measured)** comes from the headless
> mGBA experiment described under "Evidence", every number marked **(est.)**
> is arithmetic or recollection and must be confirmed in Phase 0/1 before
> anything depends on it. Once it lands, rewrite this as the usual "how it
> works and why" spec and link it from CLAUDE.md, overview.md and
> firmware-platform.md.

## Goal

Replace the ESP32-C3 SuperMini + 1.8" 128x160 ST7735 with an **ESP32-S3
(with PSRAM)** + **3.5" 320x480 ILI9486**, used in landscape (480x320), so
that:

- GBA (native 240x160) fills the screen exactly at 2x, with no smoothing and
  no letterbox; SNES (256x224) fits with bars.
- The emulator stream runs at **30 fps**, sent at the console's **native
  resolution** (today it is area-averaged down to 160x107 on the PC, which
  looks bad when enlarged — see Evidence).
- Everything else (Face, themes, Pong, RPG, weather/clock badges, boot
  animation) keeps working unchanged.
- The existing C3 + ST7735 build keeps working (separate PlatformIO env,
  capability-negotiated stream size). Changing the board and changing the
  display are two separate risks; the phases below keep them separate.

Non-goals: emulating inside the S3 (GBA is out of reach, SNES marginal — the
PC stays the emulator host, see specs/sender-gba.md), touch input (modules
usually carry an XPT2046; leave it unwired), sound on Peemo, USB High Speed
(neither S3 nor C3 has it), 5 GHz WiFi.

## Why the link, not the screen, is the real constraint

USB on the ESP32-C3/S3 is **Full Speed, ~1 MB/s** usable **(est.)**. Sending
**frames** (RGB565):

| Frame | Bytes | At 30 fps |
|---|---|---|
| GBA native 240x160 | 75 KB | 2.25 MB/s — does not fit raw |
| SNES native 256x224 | 112 KB | 3.4 MB/s — does not fit raw |
| Any 480x320 sent whole | 307 KB | 9.2 MB/s — impossible |

So the rule is: **the PC sends native resolution, compressed and delta-coded;
the S3 scales 2x locally while writing to the panel.** The 2x enlargement
never crosses the link.

## Evidence (why native + delta, and why not 160x107 stretched)

Headless mGBA (same `mgba_libretro.dll` and FireRed ROM the Sender's
"Rodar ROM" test uses), Python harness in the session scratchpad:

- **Quality**: the current 160x107 frame stretched to 480x320 looks bad (text
  thick, small icons unreadable; bilinear only blurs it). Native 240x160
  doubled to 480x320 looks right. The size of the picture does not add detail;
  only sending more source pixels does. **RGB332** (8-bit) was also tried and
  rejected: visible banding, hue shifts; dithering just adds a grid.
  RGB565 stays.
- **Bandwidth at 30 fps** (every other emulated frame, 2249 frames of the
  intro/menus/name-entry sequence, delta against the previous sent frame)
  **(measured)**:

  | Coding | mean | p90 | worst |
  |---|---|---|---|
  | Full RGB565 frame | 75 KB | | |
  | Row-diff only (changed rows, whole row) | 10.6 KB | 40 KB | 75 KB |
  | XOR delta + zlib (proxy for delta + LZ/RLE) | 0.8 KB | 2.1 KB | 11.6 KB |

  Row-diff alone averages 0.33 MB/s but its p90 (1.24 MB/s) and worst case
  (2.3 MB/s) exceed the link; delta + compression fits everywhere with a
  wide margin. Worst frames for row-diff (all 160 rows changing: fades/solid
  transitions) compress to ~1.6 KB.
- **Not measured**: overworld walking and battles (full-screen scroll — the
  realistic worst case for compression), SNES, and the CPU cost of
  decompressing on the S3. Phase 0 exists to close this gap.

## Hardware

### ESP32-S3

- Needs **PSRAM**. Plan assumes a module in the 8 MB class, but **verify the
  PSRAM type** (quad vs octal): octal PSRAM occupies GPIO 33–37 on the usual
  modules, which removes pins the display bus would want. Fix the pin map
  against the actual board's pinout before committing to wiring.
- Board size: a SuperMini-class / XIAO-class S3 is enough; do not choose by
  connector alone — count free GPIOs (16-bit parallel bus needs ~20).
- **USB**: S3 has both the USB-Serial/JTAG peripheral (same as today's C3) and
  a full OTG controller. Today's setup (`ARDUINO_USB_CDC_ON_BOOT=1`,
  `ARDUINO_USB_MODE=1`, see specs/hardware-quirks.md) uses Serial/JTAG.
  Phase 1 must measure `STREAM` throughput on **that** path first, and only
  then decide whether to move to OTG + TinyUSB CDC (mode 0), which changes how
  the board enumerates, how DTR/RTS behave and how flashing enters the
  bootloader — re-read hardware-quirks.md before touching it.
- Avoid the S3 strapping pins (GPIO 0, 3, 45, 46) and USB D+/D- (19/20) for
  the display bus.
- WiFi stays 2.4 GHz (S3); TCP path and `HOST USB` switching are unchanged.

### ILI9486 module — **decision gate before any code**

"3.5 inch 320x480 ILI9486" modules come in two very different flavours; which
one is in hand decides the driver:

| | 8-bit (or 16-bit) parallel (Arduino-shield style) | SPI (Raspberry-Pi style) |
|---|---|---|
| Fits S3 peripheral | LCD_CAM in **I80** mode, DMA, no CPU | SPI master + DMA |
| Colour over the bus | RGB565, 2 bytes/px | usually **18-bit, 3 bytes/px** **(est.; confirm for the exact module)**, or 16-bit with the vendor's quirky framing |
| Full 480x320 frame | 307 KB @ 16 bpp | 460 KB @ 18 bpp |
| Bandwidth need at 30 fps | 9.2 MB/s | 13.8 MB/s — beyond a realistic SPI clock |
| Verdict | **Preferred.** Headroom even at full-frame writes | Workable **only** by writing just the changed rows/rects |

**Recommendation: buy/use the parallel variant.** If only the SPI variant is
available, the dirty-row write (below) is mandatory, not an optimisation, and
the achievable fps in scroll-heavy scenes must be re-measured.

Other module notes:

- Native orientation is 320x480 portrait; landscape 480x320 is set with the
  controller's memory-access-control register (MADCTL), like `rotation 1`
  does today for the ST7735.
- Backlight draws noticeably more than the 1.8" panel; check the USB port and
  the existing power path are fine. Use a MOSFET/PWM pin if brightness
  control is wanted, not a GPIO direct.
- Keep the ILI9486 datasheet's write-cycle limit in mind when choosing the
  bus clock; some modules run out of spec and still work, but tune and test,
  don't assume.

## Firmware (BrobotCore)

### Structure

- New `BrobotCore/src/ILI9486PhysicalDisplay.{h,cpp}` implementing `IDisplay`
  (same role as `ST7735PhysicalDisplay`, specs/architecture.md). The
  `StreamMode` fast path (`writeStreamRow`) gets a counterpart, not a change
  to `IDisplay`, same precedent as today.
- New PlatformIO env `esp32s3_physical` (`board = esp32-s3-devkitc-1` or the
  exact board; `board_build.arduino.memory_type` / PSRAM flags per module).
  `esp32dev_physical` (C3 + ST7735) stays as is.
- `LOGICAL_WIDTH/HEIGHT` (`Config.h`, now 160x128) must become per-env;
  the two display classes define the physical size, the logical canvas is a
  separate decision (next section).
- Pins, `TFT_*` constants and the bus choice move into the new env's
  `build_flags`/header; do not reuse the C3 pin constants.

### Logical canvas vs physical panel

All of Face/Personality/themes/games draw into a 160x128 canvas today.

- **Phase 2 (low risk)**: keep the 160x128 logical canvas (41 KB, internal
  RAM) and let `present()` enlarge **2x** to 320x256, centred on the 480x320
  panel (bars: 80 px left/right, 32 px top/bottom). Zero changes to
  Face.cpp, themes, Pong, RPG. The CRT `SCANLINES` filter becomes natural:
  dim every second output row.
- **Later, optional**: a native **240x160** logical canvas (same size as GBA)
  scaled exactly 2x to 480x320, which fills the panel with no bars. This means
  re-laying out Face/themes/games for a different logical size, so it's its own
  project; do it only if the bars bother.
- Do **not** scale 2.5x (160x128 -> 400x320): uneven pixel widths ruin pixel
  art.

### Scaling and memory

- Stream target canvas stays **native** (240x160x2 = 75 KB, internal SRAM) —
  the S3 does not need a 307 KB 480x320 buffer; `present()` reads rows from
  the native canvas, repeats each pixel and row 2x into a small DMA line
  buffer, and pushes. PSRAM is then only a convenience (face canvas, future
  larger logical canvases), not a requirement of the stream path.
- **Dirty-row writes**: the delta already says which rows changed; write only
  those to the panel (`setAddrWindow` per run of rows). Mandatory on SPI,
  cheap win on parallel.
- SNES 256x224: 2x (512x448) does not fit 480x320. Options, pick in Phase 4:
  1x centred (256x224 on black), or a fitted ~1.43x (366x320) with nearest
  sampling via a precomputed column map (slightly uneven pixels). Start with
  1x.

### Stream decode

- Decompress on the S3 (LZ4 block, BSD-licensed, tiny; or a custom RLE if LZ4
  is overkill — decide from Phase 0 numbers), apply into the native canvas,
  then `present()` the dirty rows. **Decompress + apply CPU time is not
  measured yet** (Phase 1).
- Keyframe (full frame) sent at start and every N seconds / on resolution
  change / after a dropped ack, so a lost frame cannot leave garbage.

## Protocol (PROTOCOL.md is the source of truth)

Changes to specify **there first**, then mirror here:

- `PING` reply advertises the display: width x height, stream formats/codecs
  (`PEEMO 3`, or a capability field — prefer a capability field so older
  Sender/firmware combinations keep working). The Sender chooses the stream
  size from it; today's hardcoded 160x128 (`GbaSession.OutWidth/OutHeight`)
  goes away.
- `STREAM START <w> <h> <fmt> <codec>` (today's `STREAM START` is bare).
- `FRAME <seq> <bytes>`: payload is a codec-specific delta (changed
  rows/rects + pixel data), with a keyframe flag. `FRAMEOK <seq>` ack
  unchanged; keep the existing pacing (up to 2 frames in flight, byte budget
  on USB only — see `GbaSession`).
- Backward compatibility: a C3/ST7735 Peemo keeps receiving today's
  160x107-style frames (`fmt` = RGB565 rows, no codec).

## PC side (Brobot.Sender)

- `GbaSession.OnFrameReady`: stop downscaling to a fixed 160x128. Take the
  display size from the connected board; if it reports native-capable, send
  the core's native frame (GBA 240x160, SNES 256x224) through the delta/codec
  path; otherwise fall back to today's area-average downscale.
- Pace at **30 fps**: the core runs at ~59.7 fps (audio-paced, see
  sender-gba.md); send one frame out of every two.
- `ConsoleProfile` gets the native size per console (it already maps the
  console; the `OutWidth/OutHeight` constants become per-connection state).
- The "Modo teste" stream card (Phase 0 harness in MainWindow) gets a mode
  for native sizes and compressed deltas so the link can be measured without
  a ROM.

## Phases

0. **Measure gameplay deltas (PC only, no hardware).** Extend the headless
   mGBA harness with scripted inputs (leave menus, walk the overworld, enter a
   battle), and SNES via the Snes9x core. Output: bytes/frame distribution
   for row-diff and for the chosen codec on realistic scenes, including scroll.
   **Gate**: if the codec's p99 frame exceeds what the link can move in one
   frame interval (~33 ms ≈ 33 KB on USB FS), reconsider — cut scroll scenes to
   20 fps, or use the WiFi path for the stream.
1. **S3 bring-up with the existing 1.8" ST7735** (display change not yet
   involved): new env, PSRAM check, USB CDC, WiFi/portal, buzzer, everything
   that runs today on the C3. Measure `STREAM`/`FRAME` throughput over USB and
   WiFi, plus LZ4/RLE decode time on the S3. This isolates the board change.
2. **ILI9486 driver + 2x `present()`** of the existing 160x128 canvas. Checks:
   orientation, colour order, backlight, tearing, SCANLINES look, all themes
   and Pong/RPG unchanged.
3. **Protocol + firmware stream v3**: capability in `PING`, `STREAM START`
   with size/format/codec, native canvas, decode, dirty-row writes.
4. **Sender**: native passthrough, per-board size, 30 fps pacing, delta +
   codec, SNES fit option, fallback for the C3. Update the test card.
5. **Docs/build**: specs/build-and-run.md (new env, pin table, flash steps),
   hardware-quirks.md (S3 USB/PSRAM/strapping notes), architecture.md,
   firmware-platform.md, PROTOCOL.md, installer if the build artefacts change.

## Risks and open questions

- **Which ILI9486 module exactly** (parallel vs SPI, 16- vs 18-bit over SPI):
  decides the driver and whether 30 fps is comfortable or tight. Confirm
  before buying/wiring.
- **Scroll-heavy scenes** are unmeasured; the whole 30 fps claim rests on
  Phase 0.
- **USB Full Speed ceiling (~1 MB/s, est.)** and whether Serial/JTAG on the S3
  reaches it; OTG/TinyUSB may add throughput but changes enumeration/flash
  behaviour.
- **S3 CPU cost** of decode + apply + 2x scaling + dirty-row DMA at 30 fps, on
  top of WiFi/Personality work in the same loop.
- **Pin pressure**: 16-bit parallel + backlight + buzzer + USB + PSRAM pins
  on a small board; 8-bit parallel is the fallback (halves the bus rate, still
  above what 30 fps needs on I80 if the clock is high enough — verify
  against the panel's write-cycle limit).
- **Face/themes on the big panel**: with the 2x/bars plan they look exactly
  as today, only bigger; a native-resolution redraw is a separate decision.
- **Power**: bigger backlight + S3 + WiFi on a bus-powered USB port.
