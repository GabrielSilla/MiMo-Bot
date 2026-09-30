# Game Boy Advance no Peemo (PLANNED, not implemented yet)

> Status: design agreed with the user, nothing built yet. This file is the
> plan to implement from; once it lands, rewrite it as the usual "how it
> works and why" spec and link it from CLAUDE.md and specs/overview.md.

> **Update — SNES and bundled cores.** The same card also runs Super Nintendo
> ROMs (`.sfc/.smc/.swc/.fig`, Snes9x core). `Gba/ConsoleProfile.cs` maps a
> ROM's extension to its console: core DLL, keyboard map, help line. Cores are
> shipped inside the app (`src/Brobot.Sender/Cores/` -> `cores\` next to the
> exe, see installer.md), each with a `NOTICE-*.txt`. `GbaSession` fits any
> core's picture into Peemo's 160x128 keeping the aspect (GBA 160x107, SNES
> 146x128 with side bars) and paces itself on the core's own fps. **License
> note:** Snes9x is non-commercial-only; mGBA is MPL-2.0. Sound is still not
> wired for either. USB/WiFi transport tuning: see PROTOCOL.md (`HOST USB`) and
> the `GbaSession` comments (2 frames in flight; byte budget on USB only).
>
> **Play time counts in the Relatório.** The emulator runs inside Peemo Sender,
> so `GameMonitor` (which watches other processes) never sees it. Every status
> tick `MainWindow` passes the playing ROM's title (`ConsoleProfile.GameTitle`:
> file name minus region/revision tags) to `DailyReportTracker.SetEmulatorGameActive`
> while `GbaSession.IsPlaying` (a game is really loaded, not just a session that
> failed to load). It lands in the same `GameSeconds`/per-game totals as a
> detected PC game — same free half hour and penalty blocks in
> `DailyReportScoring` — and a detected PC game wins the tick if both are active,
> so time is never counted twice. Stopping the card's game stops the clock.

A "Game Boy Advance" card in the Sender's Mini Games tab. The game runs **on
the PC** — emulation, sound, saves, input — and Peemo is only the screen:
every frame goes over WiFi and Peemo draws it. Nothing is shown on the PC
(no emulator window); the sound plays on the PC.

It follows **the one rule** (specs/overview.md) the same way Pong does from
the other side: Core owns the mode (it enters an exclusive "video" mode and
stops drawing its face, like `PONG START`), and the PC only sends pixels.
The Core side is deliberately generic — a video stream mode, not a GBA mode
— so the "mirror the PC screen" idea discussed earlier can reuse it later.

Decisions already taken with the user:

- **Controls: keyboard first**, through the same `GlobalKeyboardHook` Pong
  and the RPG use (works without the Sender window focused). Xbox
  controller (XInput) comes later.
- **UI: a card in Mini Games** next to Pong and Batalha RPG — pick a ROM,
  JOGAR, PARAR, and a list of recent ROMs. No tab of its own.

## Legal / what ships

- The installer ships the **emulator only**: the mGBA libretro core
  (`mgba_libretro.dll`, MPL-2.0 — the license text goes into the install
  folder and the installer's license page mentions it).
- **No games, ever.** The user picks their own ROM file (their own
  cartridge dumps); the card says so.
- No GBA BIOS needed: mGBA has a built-in replacement (HLE BIOS).

## PC side — `src/Brobot.Sender/Gba/` (names tentative)

- **`LibretroCore`**: P/Invoke over the libretro C API of
  `mgba_libretro.dll` — `retro_init`, `retro_load_game`, `retro_run`,
  `retro_get_memory_data(RETRO_MEMORY_SAVE_RAM)`, plus the callbacks:
  video refresh (240×160, XRGB8888 or RGB565), audio sample batch, input
  poll/state, environment (pixel format, system/save directories).
- **`GbaSession`**: a dedicated thread running the core at the GBA's
  59.73 fps, paced by the audio output (the usual libretro approach: audio
  is what must never stutter; video frames are dropped, not delayed).
- **Audio**: an output device on the PC (NAudio or WASAPI directly — pick
  whichever adds the least; NAudio is one NuGet package).
- **Input**: default keyboard map — arrows = D-pad, Z = A, X = B, A = L,
  S = R, Enter = Start, Backspace = Select, Esc = stop playing (same as
  Pong's Escape). Editable mapping is a later nicety.
- **Saves**: the cartridge save RAM is written to
  `%AppData%\Brobot\gba\saves\<rom name>.sav` on stop and every minute
  while playing, loaded on start. Save states come later.
- **Frame pipeline** (`GbaFrameSender`):
  1. Scale 240×160 → **160×107** (exactly 2/3, so the aspect is kept and
     pixel art stays even). Try area averaging first (every 3×3 source block
     → 2×2) and nearest-neighbour as the alternative; judge on the device.
  2. Convert to RGB565.
  3. **Lossless, changed rows only**: compare with the last frame *Peemo
     acknowledged* and send only the rows that differ (a row = 320 bytes).
     No JPEG — its artifacts wreck pixel art, and GBA frames change in
     patches, so row diffs cut most of the volume on their own. Add RLE
     inside a row only if phase 0 shows it's needed.
  4. **One frame in flight**: send a frame, wait for Peemo's ack, then send
     the *latest* emulated frame (older ones are simply skipped). This is
     what keeps latency flat — the link can never build a backlog, the
     frame rate just settles at whatever the link sustains.
- **Holds**: while a game runs, Pensamentos holds its turn (add to
  `ThoughtHoldReason`), and the other Sender features treat it like a game
  running (`_gameRunning`-style: no FACE/MSG spam into a screen that isn't
  showing them).

## Core side — video stream mode

- `STREAM START` enters an **exclusive mode** like Pong: Personality/Face
  stop updating and rendering; the frame buffer (the existing
  `GFXcanvas16` in `ST7735PhysicalDisplay`) is cleared to black once and
  then only receives stream data. `STREAM STOP` (or no frame for ~3s — the
  PC app died) returns to Personality on the next frame.
- **Binary frames inside the text protocol**: a header line
  `FRAME <seq> <bytes>` followed by exactly `<bytes>` of binary payload.
  `Protocol` gets a small binary mode: after that header it stops treating
  bytes as text and fills a payload buffer until the count is reached,
  then hands it over. Payload = a list of row updates:
  `[row y (1 byte)] [160 × RGB565 big-endian (320 bytes)]`, drawn at the
  letterboxed position (y offset 10: the 160×107 image leaves 21 rows,
  10 above / 11 below).
- After applying a frame: `present()` and reply `FRAMEOK <seq>` — the ack
  the PC waits for.
- The CRT filter (`SCANLINES`) applies as usual — arguably a feature for a
  retro console. It can be switched off with the existing card.
- The Brobot Virtual Display (simulator) is out of scope at first: it
  draws through text commands, and full frames that way would crawl.
- `PROTOCOL.md` documents `STREAM` and `FRAME` (and the PING revision goes
  to `PEEMO 2`, so the Sender can tell a board that understands it).

## Numbers to confirm (phase 0)

- Full frame raw: 160×107×2 = 34 KB. At 30 fps that's ~1 MB/s — on the edge
  of what an ESP32-C3's WiFi sustains in practice; with row diffs the
  typical frame should be far smaller.
- Drawing: pushing the 40 KB canvas over SPI takes ~10–15 ms, fine.
- Target: **20–30 fps on Peemo** with the game at full speed on the PC,
  and a delay small enough that the image doesn't feel behind the sound.

## Implementation order

1. **Phase 0 — measure before building**: `STREAM`/`FRAME` in the
   firmware plus a Modo teste button that streams a synthetic moving
   pattern (full frames and partial frames) and reports the fps and
   round-trip time it gets. Decides whether row diffs are enough or RLE is
   needed, and what fps to expect.

   **Built** (protocol + firmware + PC test harness): `StreamMode` (a
   third exclusive mode alongside `PongGame`/`RpgBattle`), `Protocol`'s
   binary-mode payload parsing, `ST7735PhysicalDisplay::writeStreamRow`
   (a direct-canvas fast path, not part of `IDisplay` — same precedent as
   `setScanlinesEnabled`), and `PROTOCOL.md`'s `STREAM`/`FRAME`/`FRAMEOK`
   section (`PEEMO 2`). On the PC side, `BrobotConnection.SendRawBytes`
   (a binary write path alongside the existing text `SendCommand`) and a
   "Teste de stream (GBA, Fase 0)" card under Modo teste that drives a
   synthetic scan-bar pattern (full frames every 30th tick, row-diff-only
   otherwise) and reports rolling fps/RTT, separated by full vs. partial.
   **Measured on real hardware: ~20fps** with the Modo teste button's
   synthetic pattern (full frame every 30th tick, row-diff-only otherwise)
   — inside the spec's 20-30fps target, and enough for actual GBA gameplay.
   Row diffs alone are enough; Phase 1 doesn't need RLE on top.

   Three fixes along the way, none obvious from the protocol design itself
   — worth knowing before touching this code again:
   - **Nagle's algorithm** was stalling every `FRAME`→`FRAMEOK` round trip
     by tens to hundreds of ms. Invisible for every other command in this
     protocol (fire-and-forget, nothing waits on a reply), but `STREAM`'s
     one-frame-in-flight pacing is a tight request-response loop — exactly
     the pattern Nagle+delayed-ACK punishes. Fixed with
     `TcpClient.NoDelay = true` in `BrobotConnection.ConnectTcp` (C#) and
     `WiFiClient::setNoDelay(true)` on the accepted `protocolClient` in
     `main.cpp` (firmware) — `PeemoDiscovery.cs` already had the C# half of
     this precedent for its own probe connections, for the same reason.
   - **ESP32 WiFi modem sleep** (`WIFI_PS_MIN_MODEM`, the default) dozes the
     radio between the AP's DTIM beacons, adding latency invisible to an
     occasional `FACE`/`MSG` line but real for a tight loop. Fixed with
     `WiFi.setSleep(false)` in `WifiSetup.cpp`'s `tryConnectSavedNetworks`
     — Peemo is mains-powered, so there's no battery-life tradeoff to
     weigh. Kept even though it turned out not to be the dominant cost
     here: it independently fixed a different bug the user had been
     hitting.
   - **The real bottleneck**: `WiFiClient::available()` on ESP32 issues a
     fresh `lwip_ioctl(FIONREAD)` syscall on every call, and
     `Protocol::poll()`'s loop condition was calling it once per byte. Free
     for a 20-byte `FACE`/`MSG` line; thousands of syscalls per `FRAME`
     payload, and the dominant cost by far (~9fps even after both fixes
     above). Fixed by snapshotting `available()` once per `poll()` call
     instead of once per byte — see `Protocol.cpp`'s comment there.
2. Libretro host: load mGBA, run a ROM headless, sound on the PC,
   keyboard input — checked on the PC alone first (dump frames to a file).
3. Frame pipeline: scale, diff, one-in-flight sending — the first playable
   version on Peemo.
4. The Mini Games card: pick ROM, recent list, JOGAR/PARAR, key map shown
   on the card; saves.
5. Installer: ship `mgba_libretro.dll` + license, NAudio if used.
6. Docs: rewrite this file as a real spec, update PROTOCOL.md (done in 1),
   specs/overview.md, CLAUDE.md, sender-minigames-achievements.md.

Later: Xbox controller, save states, editable key map, and screen
mirroring on top of the same `STREAM` mode.
