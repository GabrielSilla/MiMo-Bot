# Brobot

A desktop buddy robot. Brobot Core is one independent project that never
links against anything on the PC side — it only talks a text-line serial
protocol. On the PC side, Core is always "the device": any number of
independent Windows apps can each connect to it directly, the same way more
than one app could each open a COM port to talk to an Arduino at different
times (real COM ports only ever have one owner at a time; Core's native/TCP
build additionally accepts several simultaneous connections, purely as a
dev convenience the real serial link can't offer — see native/README.md).

```
                                    <--serial (COM) or TCP-->  Brobot Virtual Display (watches)
Brobot Core (Arduino/native, C++)
                                    <--WiFi TCP-->               Brobot Sender (sends FACE/MSG/WEATHER/TIME)
   personality + animation              both are dumb clients, no Brobot logic
```

Sender only ever reaches Core over WiFi TCP now (see BrobotConnection internals below
for why Serial got dropped from that side); Virtual Display still supports both.

## The one rule that matters

**PC-side apps render or send. Brobot Core decides.** Neither Brobot Virtual
Display nor Brobot Sender may ever contain Brobot states, emotions,
blink/look logic, or hardcoded messages. Virtual Display only implements
`IDisplay` (Clear/DrawPixel/DrawRect/FillRect/DrawRoundedRect/DrawText/
DrawBitmap) and renders whatever it's told; Sender only turns a checked
checkbox (or, behind the scenes, a Windows media-session event, a weather
API response, the system clock) into `FACE`/`MSG`/`WEATHER`/`TIME` command
lines — it never decides what a state *means*, e.g. it doesn't pick which
expression "rain" maps to, Core does. All personality (blinking, looking
around, sleeping, expressions, message typing/timing) lives in Brobot Core.
This split is intentional so that a future `ST7735Display` (real hardware)
can drop in next to `SimulatorDisplay` without either one knowing the other
exists, and so any number of PC-side tools can be added later without ever
needing to know Brobot's actual behavior.

## Repository layout

```
BrobotVirtualDisplay.slnx           .NET solution (open with `dotnet build`)
src/
  Brobot.Display.Abstractions/      IDisplay + DisplayColor — the shared contract
  Brobot.Display.Simulator/         WPF app: SimulatorDisplay, MainWindow, SerialDisplayBridge, Font5x7,
                                     AurebeshFont (THEME MI2MO2's alien script, see Face.cpp below)
  Brobot.Connection/                C#: BrobotConnection — shared COM/TCP client used by both
                                     Simulator and Sender to reach Core — plus MimoDiscovery, which
                                     finds MiMo's DHCP-assigned IP on the network (see Architecture below)
  Brobot.Sender/                    WPF tray app, branded "MiMo" to the user, for whoever assembled a
                                     Brobot: MainWindow is a card-based checklist (Conexão, Hora, Clima,
                                     Pausa, Atividade da IA, Mídia, Jogos, Tema, Sons, Scanlines — mostly
                                     checkboxes, except Conexão (a status readout plus one button), Tema
                                     (a ComboBox) and Atividade da IA (an Instalar/Desinstalar button), see below).
                                     The Conexão card is a readout, not a setup form — MiMo's address is
                                     discovered, never typed (WiFi/TCP only — no SettingsWindow, no
                                     Serial/USB, see below). WeatherMonitor + WindowsMediaMonitor + GameMonitor +
                                     AiThoughtsListener are the live data sources so far, ClaudeCodeHookInstaller edits
                                     Claude Code's own settings.json, SenderSettings persists
                                     checkbox/provider/connection state to %AppData%. Icons via the
                                     MahApps.Metro.IconPacks.Material NuGet package.
hooks/                               mimo-claude-hook.ps1 (the Claude Code hook command) and
                                     mimo-claude-statusline.ps1 (its statusLine command — a different
                                     contract, see below); both wired up by ClaudeCodeHookInstaller and
                                     copied to Brobot.Sender's build output (see its csproj) rather
                                     than run from here.
BrobotCore/                         PlatformIO project (Arduino/C++)
  include/, src/                    Config, IDisplay, SerialVirtualDisplay, ST7735PhysicalDisplay,
                                     WifiSetup (ESP32 only — WiFi provisioning, see below),
                                     Buzzer (R2D2-style beeps, see below — firmware-only, not part
                                     of the native build's shared source list), DeviceSettings
                                     (the SOUND/SCANLINES toggles, header-only),
                                     AurebeshGFXFont.h (THEME MI2MO2's alien script as an Adafruit
                                     GFXfont — the firmware-side twin of AurebeshFont.cs),
                                     Face, Personality, Protocol, main.cpp
  platformio.ini                    envs: uno, uno_physical, esp32dev, esp32dev_physical
                                     (esp32* envs use board=esp32-c3-devkitm-1 — the actual
                                     hardware in hand is an ESP32-C3 SuperMini clone board)
  native/                           Dev-only build of Face/Personality/Protocol as a Windows .exe —
                                     no Arduino required. Listens on TCP the same way a real device's
                                     COM port is "the thing PC apps connect to". See native/README.md.
                                     Not part of the PlatformIO project; built separately with MSVC.
PROTOCOL.md                         Single source of truth for the serial wire protocol
```

## Architecture

- **`IDisplay`** is mirrored by hand in both languages (`Brobot.Display.Abstractions/IDisplay.cs`
  and `BrobotCore/include/IDisplay.h`) — same method set, same 160x128 logical
  coordinate space in both.
- **`SimulatorDisplay`** (C#) implements `IDisplay` over an in-memory framebuffer,
  uploaded to a `WriteableBitmap` on `Present()`.
- **`SerialVirtualDisplay`** (C++) implements `IDisplay` by serializing every call as
  a protocol line over `Serial` — it never renders anything itself.
- **`ST7735PhysicalDisplay`** (C++) implements `IDisplay` via Adafruit_ST7735/GFX for
  real hardware — hardware-tested and working on an ESP32-C3 SuperMini + a 128x160
  ST7735 clone (`INITR_BLACKTAB`, rotation 1; see the file's own comments before
  changing either — GREENTAB and rotation 3 were both tried and came out wrong,
  color and orientation respectively, on this exact panel). Draws go into an in-RAM
  `GFXcanvas16` framebuffer, not straight to the panel — `present()` is the only
  point that touches SPI, pushing the finished frame in small per-scanline chunks
  (not one big burst, which came out sheared — looked like an ESP32 SPI/DMA issue
  specific to very large single transfers).
- **`vscreen`** is a compile-time flag (`Config.h`, `-D VSCREEN=1/0` in `platformio.ini`)
  that picks `SerialVirtualDisplay` vs `ST7735PhysicalDisplay` in `main.cpp`.
- **`Personality`** owns all timing/state (blink, look-around, sleep, expression,
  message typing) and produces a `FaceState`. **`Face::render`** turns a `FaceState`
  into `IDisplay` calls. Neither knows about serial or WPF.
- **`BrobotConnection`** (C#, `Brobot.Connection`) is the one place that knows how
  to reach Core — `ConnectSerial` (COM) or `ConnectTcp` (native dev build, *or* a
  real ESP32's WiFi TCP server now — see PROTOCOL_TCP_PORT below; both speak the
  same port/protocol so it's just a host field), with auto-retry so app/Core
  startup order and Core restarts don't matter — and how to batch its lines into
  frames (a `PRESENT` line ends one). Both `Brobot.Display.Simulator` (interprets
  frames as draw calls) and `Brobot.Sender` (mostly just sends `FACE`/`MSG` and
  ignores incoming frames) sit on top of it, so the COM-vs-TCP handling and a
  subtle encoding bug (below) only had to be fixed once instead of in two copies.
  `Brobot.Sender` only ever calls `ConnectTcp` — `.NET`'s `SerialPort` proved
  unreliable against the ESP32-C3 SuperMini's native USB-CDC port (see Known
  hardware quirk below), so Serial support stayed in this shared class for
  `Brobot.Display.Simulator` but was dropped from Sender's own UI entirely.

## Protocol (see PROTOCOL.md for the full spec)

Text lines over Serial (COM), or over a plain TCP socket — either the
native/TCP dev build, or a real ESP32's own WiFi TCP server (`WifiSetup.cpp`
+ `PROTOCOL_TCP_PORT` in `Config.h`, port 5555) — same line protocol either
way. Control commands flow PC→Core, draw commands flow Core→PC (only when
`vscreen=1`, or always in the native build):

```
FACE <NEUTRAL|HAPPY|SAD|ANGRY|SLEEPING|SLEEPY|COFFEE|MUSIC|WATCHING|ERROR|READING|FINISHED|THINKING|PLAYING|IDLE|IDLE_GAME|IDLE_MEDIA>
MSG <text>                    (empty text clears the message)
WEATHER <tempC> <condition>   (CLEAR|CLOUDY|RAIN|STORM|SNOW|FOG; empty clears the badge)
TIME <HH:MM>                  (empty clears the clock)
NOTIFY <FACE> <text>          (top-priority full-screen interruption, auto-clears after 10s)
THEME <DEFAULT|MATRIX|MI2MO2|MI84> (persistent, like WEATHER/TIME — see Face.cpp's theme notes)
SOUND <ON|OFF>                (persistent; buzzer cues, see Buzzer.cpp)
SCANLINES <ON|OFF>            (persistent; physical display's CRT post-FX only)
STATS <cpu%> <cpuTempC> <gpu%> <gpuTempC> <ram%>   (-1 = no source; empty clears)
PING                          (Core replies "MIMO 1" — the only command it answers)

CLR r g b
PIXEL x y r g b
RECT/FILLRECT x y w h r g b
RRECT x y w h radius r g b
TEXT x y r g b <LATIN|AUREBESH> text...
PRESENT
```

`PING` is the one command that flows *back* Core→PC, and the only one that
touches neither `Personality` nor `DeviceSettings` — it's about the link, not
about Brobot. It exists so a PC app can find MiMo on the network after DHCP
moves it; see `MimoDiscovery` under Brobot.Connection internals below.

`FACE`/`MSG` are arbitrated by **four** independent priority tiers on Core,
not by whichever PC app last happened to send one — see Personality.cpp
below. Highest first:

```
NOTIFICATION  >  FOREGROUND (AI)  >  GAME  >  MEDIA
```

**Notifications** (`NOTIFY`) are the things MiMo interrupts you *for* —
Pausa's break reminders, Clima's weather-change alerts, the bedtime nudges.
They outrank everything including AI activity, take the entire frame (no
badges, no log, no message box — see `FaceState::isNotification` and
`drawNotificationScreen`), and expire on their own after
`NOTIFICATION_DURATION_MS` (10s — raised from 7 because the message types
itself in inside that same window, so a long phrase spent most of a 7s one
still appearing, and because both dedicated animations want room to play
more than once).

MiMo's **face stays visible in every notification**. An earlier version gave
the whole frame to the artwork, so a notification with no illustration of
its own fell back to an empty framed card — unreadable, because it wasn't
depicting anything. Making the eyes the constant and the artwork the
optional extra removed the need for a placeholder at all. `COFFEE` puts
small eyes left and slightly *above* the cup's resting line, with the
steaming mug to the right; every ~2.8s that mug rises and drifts toward the
face, holds, and settles back onto the saucer — MiMo taking a sip. The
gesture is pure translation: there is no rotation at this resolution, and a
tilted mug built from fillRects reads as a broken one rather than a tipped
one. (Its handle was also wrong until now — the inner cut's right edge
landed exactly on the outer block's, erasing the whole right wall, so the
"handle" was two prongs with nothing joining them. A real bug, fixed once.) `WEATHER` puts him beside an umbrella with rain
falling behind both — drawn before the canopy and the eyes, so drops that
would land on the umbrella are simply painted over, which reads as shelter
with no per-drop collision test. Its wire token is just `WEATHER`: the
artwork is chosen from `FaceState::weatherCondition`, which the `WEATHER`
command already keeps current for the badge, so the alert and the badge
cannot disagree and each further condition costs one `case` rather than a
command and an enumerator of its own (RAIN/STORM get the umbrella; CLEAR gets a
sun turning in the top-right corner — a stepped disc plus 8 three-block
rays placed by angle, one turn every 6s, using only `sin` with the cosine
taken as `sin(t + pi/2)` since the native build's `Arduino.h` shim never
wires up `<math.h>`; the rest show him alone and centred, since an umbrella
beside a "clear skies" alert would contradict it). This is also why `Brobot.Sender` sends `WEATHER`
*before* the `NOTIFY WEATHER` — the other order would illustrate the alert
with the previous condition. `SLEEPY` makes the eyes *be* the
animation — a 3.6s cycle of lids sagging shut on a squared curve, a beat
held nearly closed, a startled snap open past normal size, then three quick
blinks — anchored on `FaceState::notificationStartedMs` rather than raw
`nowMs` so it always begins awake; everything else just blinks normally.
Every notification drawing takes the **background color as a parameter**
rather than assuming black: the "cut a gap" trick this codebase uses
everywhere fills the cut with the background, and MI2MO2's ground is R2's
light plate, so assuming black gave the mug two black holes and each eye
four dark specks at its corners. Two real bugs, fixed once — `drawEye`/
`fillRoundedRect` now default those parameters to black so no existing
caller changed. Nothing is restored afterwards because
nothing was displaced: no lower tier is touched to make room, so the frame
after expiry just draws what was always underneath. They arrive as one
atomic line rather than the usual `FACE`+`MSG` pair precisely because they
are the top priority — a two-command form could be caught half-applied, and
a lone `MSG` would be routed by `_lastCommandTier` instead. Core raises the
bedtime one itself, with no PC app involved (`raiseNotification`, which is
why that is a separate function from `onNotifyCommand`).

**GAME and MEDIA** used to be a single shared BACKGROUND tier where
whichever arrived last won, so starting music during a game replaced
"Jogando X" with the track and never brought it back. They rank against each
other now — a game is what you're actually doing, music is what's on behind
it — which is what forced `FACE IDLE` to grow the `IDLE_GAME`/`IDLE_MEDIA`
variants: a single "clear the background" no longer says which of the two it
means. Plain `IDLE` still clears both, which is what a client predating the
split intends by it.
**Foreground** (`THINKING`/`READING`/`FINISHED`/`HAPPY`/`SAD`/`ANGRY`/
`SLEEPING`/`SLEEPY`/`COFFEE`/`ERROR`/`NEUTRAL`) always wins the render while
active — most of these are driven by Atividade da IA, but `SLEEPY` is
autonomous (see below) and `COFFEE` is driven by Pausa's own two daily
reminder times, not the AI. **Background** (`MUSIC`/`WATCHING`/`PLAYING`, driven by
Mídia/Jogos) holds underneath it and reappears automatically — face and
message both, with no resend needed — the moment foreground releases the
screen. `FACE NEUTRAL` clears only foreground; `FACE IDLE` clears the
game and media tiers (`FACE IDLE_GAME`/`FACE IDLE_MEDIA` clear one each) — sending the wrong one for the intent leaves the other tier
stuck, so Mídia/Jogos must send `IDLE` (never `NEUTRAL`) to release their own
sticky expression.

`WEATHER`/`TIME` are persistent top-corner overlays, independent of
`FACE`/`MSG` — they don't expire, interrupt, or get interrupted by them
(see Face.cpp below). Core has no RTC or network of its own, so both are
only ever as fresh as whatever PC app last pushed them.

**Resolution is 160x128 (landscape)** and must stay identical in
`SimulatorDisplay.LogicalWidth/Height` (C#) and `Config.h`'s
`LOGICAL_WIDTH/HEIGHT` (C++) — nothing enforces this automatically.

## Build & run

```bash
# WPF app
cd C:\Projects\MiMo-Bot
dotnet build BrobotVirtualDisplay.slnx
# exe at src\Brobot.Display.Simulator\bin\Debug\net8.0-windows\Brobot.Display.Simulator.exe

# Arduino firmware (vscreen mode, no physical display needed)
cd C:\Projects\MiMo-Bot\BrobotCore
python -m platformio run -e uno
python -m platformio run -e uno -t upload --upload-port COM5
```

PlatformIO isn't on PATH as `pio` in this environment — always invoke it as
`python -m platformio`. The Uno is on **COM5** in this dev setup (COM1 is
the motherboard's built-in serial port, not the Arduino — don't connect to it).

ESP32 support (`esp32dev` / `esp32dev_physical` envs) is hardware-tested on
an **ESP32-C3 SuperMini** clone board with a 1.8" 128x160 ST7735 SPI display
(see Architecture above for the display specifics):

```bash
cd C:\Projects\MiMo-Bot\BrobotCore
python -m platformio run -e esp32dev_physical -t upload --upload-port COM6
# COM6 varies by machine — check Device Manager; the SuperMini's native
# USB shows up as VID 303A (Espressif), not a CH340/FTDI bridge chip.
```

Pin wiring (`Config.h`, chosen to avoid the SuperMini's strapping/LED pins —
see the file's own comments): `TFT_CS=IO10, TFT_RST=IO1, TFT_DC=IO3,
TFT_SCK=IO4, TFT_MOSI=IO6`. **Avoid IO2/IO8/IO9** on this board entirely —
IO2/IO8/IO9 are strapping pins sampled at boot, and IO8 also drives the
SuperMini's onboard WS2812 LED.

WiFi has no hardcoded credentials — on first boot (or if the saved network
can't be reached), the board serves its own **"MiMo-Setup"** access point
with a small captive-ish config page at `http://192.168.4.1` (scans and
lists nearby networks instead of requiring the SSID to be typed by hand;
`WifiSetup.cpp`). Submitting the form saves the credentials to flash
(`Preferences`/NVS) and reboots into station mode — see `WifiSetup.h`'s doc
comment for the exact fallback logic. While the portal is open, the screen
shows a static "connect to MiMo-Setup" `FACE FINISHED` message built
directly as a `FaceState` (bypassing `Personality` entirely, since its
FINISHED-tier message auto-expires after ~10s, which isn't wanted for a
setup screen that needs to stay up indefinitely). Once connected, `main.cpp`'s
`loop()` shows a persistent "MiMo Configurado! IP: <ip>" message (same
FaceState-bypass trick, and prints once over Serial) for as long as no PC
app is connected over TCP — not just briefly at boot; if Brobot.Sender later
disconnects, the message reappears until it (or another client) reconnects.
Once a client connects, the screen reverts to Personality's own face on the
very next frame. This screen is no longer something anyone has to act on —
Brobot.Sender finds MiMo by itself (see `MimoDiscovery`) and its Conexão card
has no field to type an address into — but it stays useful as the one place
that says, from the device's own point of view, whether WiFi came up and at
which address.

### Local dev without an Arduino — the native BrobotCore build

BrobotCore's firmware logic (`Face.cpp`/`Personality.cpp`/`Protocol.cpp`) also
compiles into a plain Windows console `.exe` — no Arduino attached, no
PlatformIO involved — so the personality/rendering code can be iterated on
and exercised against the real WPF apps without hardware. Full details live
in [`BrobotCore/native/README.md`](BrobotCore/native/README.md); this is the
short version.

**Why MSVC and not PlatformIO's own `native` platform:** PlatformIO ships a
`platform = native` target for exactly this kind of host build, but it
assumes a GCC-like toolchain (GCC/MinGW flags such as `-std=gnu++17`). This
dev machine has no gcc/MinGW, only Visual Studio, whose `cl.exe` takes
entirely different flags — so the native build is compiled directly with
MSVC via its own script instead of fighting PlatformIO's toolchain
assumptions. It's a fully separate build from `platformio.ini`; the
`uno`/`esp32dev` envs are untouched by it either way.

**Prerequisites:** Visual Studio (any recent edition) with the "Desktop
development with C++" workload installed, so `cl.exe` and `vcvars64.bat`
exist somewhere `vswhere` can find them. Nothing else — `build.ps1` finds
and loads the MSVC environment on its own (skips that step if `cl.exe` is
already on PATH, e.g. run from a "Developer PowerShell for VS").

```powershell
# 1. Build (only needed again after editing BrobotCore's C++)
C:\Projects\MiMo-Bot\BrobotCore\native\build.ps1
# -Clean wipes native\build first if you want a from-scratch rebuild.
# Output: BrobotCore\native\build\brobot_native.exe

# 2. Run it — listens on 127.0.0.1:5555 and just sits there.
C:\Projects\MiMo-Bot\BrobotCore\native\build\brobot_native.exe
# Optional args: brobot_native.exe [host] [port] — defaults 127.0.0.1 5555.

# 3. Point Brobot Virtual Display at it: click "Conectar (dev, sem Arduino)"
#    (127.0.0.1:5555 default). Either order, and both apps can be connected at once.
```

**Brobot.Sender can't reach this build as easily any more**, and it's worth
knowing why before trying. Its address field is gone (MiMo is discovered, not
typed — see `MimoDiscovery`), and a sweep deliberately skips both loopback
adapters and the PC's own address, so a Core listening on `127.0.0.1` is
invisible to it *by design*: nothing on the real network can ever be there.
The workaround is to write the address into
`%AppData%\Brobot\mimo-sender-settings.json` by hand (`"TcpHost": "127.0.0.1"`)
and click Conectar — startup ignores the saved address and sweeps, but the
button's "known address, nothing trying it" path connects directly. Running
`brobot_native.exe 0.0.0.0 5555` doesn't help on its own either, since the
sweep still won't probe this machine's own IP. Brobot Virtual Display, which
kept its address field, is unaffected.

**Core listens; PC apps connect to it** — same direction as a real COM port
(the device doesn't dial out to you) — and unlike a real COM port, the
native build accepts several simultaneous connections, so Virtual Display
(watching) and Sender (sending commands) can both be connected at once. Each
app auto-retries its connection, so Core can be killed and restarted freely
without touching either app — the normal edit/rebuild/retest loop is just
steps 1-2 again, no need to reconnect anything by hand.

Internally it carries the exact same PROTOCOL.md line protocol over TCP
instead of a UART (`native/include/TcpBroadcastStream.h` implements the same
`Stream` interface `SerialVirtualDisplay` talks to), and compiles the shared
firmware files against `native/include/Arduino.h`, a minimal stand-in for
the real `<Arduino.h>` (integer typedefs, `Stream`, `F()`, `random()` — just
enough for `Face`/`Personality`/`Protocol` to build unmodified). That shim
is only ever on the include path for this native build; the real AVR/ESP32
builds never see it.

## Firmware internals (BrobotCore)

- **`Face.cpp`**: eyes are two filled rounded-squares (42px, teal), corners faked
  with a 2-row "staircase" background cut (not a true circle — keeps serial
  bandwidth down). Eye size/position stay constant whether or not a message is
  showing. Most expressions (HAPPY/SAD/ANGRY/SLEEPING/SLEEPY) just tweak eye height/gap/offset,
  but a few replace the eye shape outright using the same "compose it from small
  `fillRect` blocks" trick: **FAILED** draws an X (`drawEyeX` — step half the block
  size, or the two diagonal strokes leave a gap exactly where they cross in the
  middle), **FINISHED** draws a "^" caret per eye (`drawEyeCaret`, self-centered in
  the eye box, since a squint alone reads as closed/sleepy rather than happy), and
  **READING** keeps normal eyes but sweeps them with a deliberately asymmetric
  timing (`readingSweep`: slow left-to-right, fast snap back — like scanning a line
  then jumping to the next one) plus a small bobbing book icon in the corner, and
  **THINKING** slices each eye into horizontal bands (`drawEyeGlitch`) and shifts
  each band left/right by a small pseudo-random offset, re-rolled every
  `GLITCH_INTERVAL_MS` (~120ms) — a deliberately jarring "signal interference"
  look, unlike the eased smoothstep motion everything else uses. `Face::render`
  has no timers/state of its own (see Face.h), so the offsets come from a small
  hash of `(nowMs / GLITCH_INTERVAL_MS, band, eyeIndex)` rather than an evolving
  `random()` seed, which is also why the two eyes glitch independently of each
  other instead of moving in lockstep. Message text word-wraps into a **fixed
  3-line window** (`MESSAGE_VISIBLE_LINES`),
  bottom-anchored; once a 4th line would be needed the oldest visible line scrolls
  off, like a terminal. Sleeping shows a bobbing "Z Z Z" near the right eye.
  THINKING has no corner icon — the glitch eyes plus the "Pensando..." message
  carry the expression on their own (in `THEME MATRIX`, see `drawMatrixRain`
  below, THINKING additionally swaps the console log for a rising-character
  "digital rain" animation in that same region — messages come back the
  moment THINKING clears). **PLAYING** (Sender's Jogos card) keeps
  eyes fully open like MUSIC/WATCHING/READING — no shape change — and a small
  bobbing gamepad icon (`drawGamepadIcon`) carries the expression instead: one
  solid body block plus a d-pad cross and two face buttons cut out of it in
  background color, the same "cut a gap from a filled block" trick the eye
  corners and the book's spine gap already use, rather than drawing new shapes
  on top. The MUSIC/WATCHING/READING/PLAYING corner icons (note/play/book/gamepad) sit at
  `CORNER_ICON_Y_SHIFT` down from the top-left — pushed down out of the fixed
  strip the WEATHER/TIME badges occupy (see below); their x-range never overlaps
  the eyes regardless of expression, so the vertical push is the only constraint.
  **COFFEE** (Sender's Pausa card) is the one expression that changes the eyes'
  size/position instead of just their shape: `Face::render` swaps in
  `COFFEE_EYE_SIZE`/`COFFEE_EYE_GAP` (smaller) and pins them to
  `COFFEE_EYES_X` near the left edge rather than centering them, clearing
  the whole right side of the frame for `drawCoffeeCup` — a saucer + hollowed-
  out mug + handle stub (same cut-a-gap trick as everything else), with three
  steam wisps that rise and sway on a continuous `nowMs % COFFEE_STEAM_CYCLE_MS`
  loop rather than a bounded one-shot animation, since they need to keep
  going for as long as the reminder message stays up. `Personality`'s
  hold-still list includes COFFEE for a concrete reason, not just
  consistency: with the eyes pinned to a near-fixed spot, an active
  look-around offset could otherwise push them past the left edge.
  `drawWeatherBadge`/`drawClockBadge` draw those two badges independent of
  `state.expression` entirely — small procedural pictograms (sun/cloud/rain/
  storm/snow/fog), same block-composition style as everything else, deliberately
  not PNG/bitmap (see PROTOCOL.md's `DrawBitmap` note — at this resolution a
  downscaled bitmap would look worse, not better, and needs a whole asset/wire
  pipeline this doesn't).
  **`Theme::MATRIX`** (Sender's Tema card, "MiMo Matrix" entry, `THEME MATRIX`/`THEME
  DEFAULT` — named `CLASSIC` in the `Theme` enum, not `DEFAULT`, since
  `<Arduino.h>` `#define`s `DEFAULT` as a macro, same class of problem as
  `Expression::FAILED` not being named `ERROR`) is a whole-frame reskin
  layered on top of everything above, not a new `Expression`: `Face::render`
  composes a `RecoloringDisplay` decorator (recolors every non-black draw
  call to a fixed green, leaving actual black — every "cut a gap" punch-out
  — alone so rounded corners/spine gaps/d-pad cutouts still read as hollow)
  the same way it already composes `DimmingDisplay` for `SLEEPING`, with
  `RecoloringDisplay` innermost so a simultaneous `SLEEPING` dims the theme's
  green instead of un-recoloring it back to teal. While `MATRIX` is active,
  every *expression* corner icon is suppressed (the music-note-through-
  gamepad icons, even Sleeping's "Z Z Z") — their info now lives in the
  console log below instead, and their usual Y-range would otherwise
  collide with it. The one exception is `COFFEE`'s cup: unlike every other
  expression, `COFFEE` already repositions the eyes themselves (small,
  pinned to the left — see below) specifically to make room for the cup,
  regardless of theme, so suppressing just the icon while still applying
  that repositioning left the eyes shrunk into the corner for no visible
  reason on screen — a real bug, fixed once — rather than a case where
  suppressing the icon and letting the log stand in for it actually made
  sense the way it does for every other expression. Since `COFFEE`'s
  eyes+cup layout sits inside the log's own region rather than tucked below
  it, `Face::render` skips the log there entirely instead of drawing over
  (or getting drawn over by) the cup — same "swap it for something else
  while this expression is active, no extra state needed" idea `THINKING`'s
  rain already uses (see below); the log reappears on its own the instant
  `COFFEE` clears. `drawWeatherBadge`/`drawClockBadge` are *not* suppressed
  either — Hora/Clima stay fixed at the top exactly as in `CLASSIC`, so the
  log starts below that strip (`MATRIX_LOG_TOP_Y`, matching
  `CORNER_ICON_Y_SHIFT`) instead of overlapping it. The eyes — still
  expression-shaped, but ~36% smaller than `CLASSIC` (`MATRIX_EYE_SIZE`/
  `MATRIX_EYE_GAP` — two successive ~20% reductions, tuned by eye rather
  than a single formula; at full size, pinned to the bottom edge, they read
  as too large/heavy for this theme) — get pinned to the bottom of the frame
  instead of the usual upper-middle spot (`COFFEE`'s own left-pinned layout
  still takes precedence over Matrix's bottom-center one, same "coffee's
  layout wins" precedence order the geometry code already had). The freed-up
  top-middle of the frame (below the badge strip) renders `Personality`'s
  own scrolling log (`_log`, `MATRIX_LOG_LINES` = 6 entries,
  `MATRIX_LOG_LINE_CAPACITY` = 74 chars each — sized so one entry can
  word-wrap across a full 3 on-screen lines, the same visible-line budget
  CLASSIC's own message box gives a single message — in `Face.h` since both
  `Personality` and `FaceState` need the same capacity) — a plain
  shift-and-append array, not a wraparound ring buffer, since the capacity
  is tiny and this keeps `Personality` handing out a simple 0..count
  chronological list with no index math. `Personality` only stores the raw
  (unwrapped) entries; `Face.cpp`'s `drawMatrixLog` is the one that greedy
  word-wraps each entry to fit the screen width (same algorithm
  `drawWrappedMessage` already uses for the normal message box) and draws
  the `> ` prompt prefix only on an entry's *first* physical line — a
  wrapped continuation is still the same message, not a new prompt, so it
  draws flush left with no prefix. This replaced an earlier version that
  drew each raw entry as exactly one unwrapped line: any entry longer than
  the screen width just ran off the right edge and was never seen at all,
  rather than merely getting cut off — a real bug, fixed once. Wrapping is
  purely `Face.cpp`'s concern, not `Personality`'s, matching the project's
  "PC-side/render layer renders, Core's logic layer decides" split one level
  down. Physical (wrapped) lines scroll off the *top* of the log, oldest
  first, once there are more than fit above the pinned eyes
  (`MATRIX_LOG_BOTTOM_GAP` below `MATRIX_EYE_SIZE`'s position) — the same
  "oldest visible line drops off" idea `drawWrappedMessage` already uses for
  a single message, just applied across the whole log instead of one entry.
  While `state.expression == THINKING`, `Face::render` calls `drawMatrixRain`
  into this same region instead of `drawMatrixLog` — a grid of columns
  (`MATRIX_RAIN_COLUMN_SPACING_PX` apart) each showing a short
  `MATRIX_RAIN_TRAIL_LEN`-character run of random `MATRIX_RAIN_CHARS` that
  rises (decreasing row) and wraps back in from the bottom edge once it
  exits off the top, each column at its own hashed speed/phase so they don't
  move in lockstep — same "no persistent state, driven purely off nowMs"
  approach `drawEyeGlitch` already uses, via its own `matrixRainHash` (kept
  separate from `glitchHash` so the two effects' timings don't correlate).
  Because this only swaps what gets drawn into the log's region for exactly
  as long as `resolveExpression` keeps reporting THINKING, there's no
  animation-start/stop state to track: the log reappears on its own, with
  no special-casing, the instant THINKING clears and rendering falls back to
  `drawMatrixLog` again next frame.
  A **third tab, MONITOR**, holds the game being played and — uniquely among
  the tabs — draws MiMo's machine stats under it (`drawMatrixMonitor`, see
  `FaceState::hasStats` and PROTOCOL.md's `STATS`). A game's "Jogando X" used
  to land in the media tab beside music and video; it moved here because it's
  the one entry with numbers to show beneath it. `drawMatrixLog` now returns
  the y it stopped at, purely so the monitor tab can start its stat lines
  below a game name of whatever height it happened to wrap to, and stops
  early rather than drawing over the eyes if a long name eats the room.
  Which tab is live still follows what the face itself is doing, with no
  extra state: PLAYING shows MONITOR, other background expressions show
  MEDIA, anything else shows AI.
  This log only ever carries the
  "prompt"-style messages (AI activity, media, games, Pausa, bedtime) — it's
  unchanged by this; the messages that do show there render exactly as
  before. It deliberately does *not* carry a time/weather line — Hora/Clima
  already render as their own persistent badges (see `Face::render` below),
  so a periodic log entry restating the same values would just be noise now
  that both are on screen at once; an earlier version of MATRIX did log a
  once-a-minute `"HH:MM - tempC - condicao"` line here, back when the
  badges were suppressed in this theme and the log was the only way to see
  that info, but it was removed once the badges came back.
  `Personality::pushLogLine` is called from exactly two places: once inside
  `onMessageCommand` (covers every message that already flows through there
  — AI hook text, media "now playing", "Jogando X", weather alerts, Pausa's
  coffee reminders — with a single call site, since the log doesn't care
  which tier a message is headed for) and once for each freshly-picked
  bedtime message.
  **`Theme::MI2MO2`** (Sender's Tema card, "MiMo Mi2-Mo2" entry, `THEME
  MI2MO2`) is a bigger departure than MATRIX: instead of reskinning a face,
  it replaces the whole frame with a close-up of R2D2's dome plate —
  `drawMi2Mo2Plate` (off-white plate via `clear`, navy inset panels, silver
  vent) then `drawMi2Mo2Lens` then `drawMi2Mo2LogicDisplay`, back to front,
  the same layering `drawMessageBox`/`drawWrappedMessage` already use.
  Every theme-specific color lives in its own `MI2MO2_*` palette rather
  than reusing `EYE_R/G/B`, because in this theme nothing is "the eye
  color".
  **This design is the second attempt, and the first one's failure is worth
  recording so it isn't retried**: the original Mi2-Mo2 was a face — a big
  red radial-gradient eye on the usual black background, on a blue band.
  It was fully built and working (single eye, pupil-shift look-around,
  dim-to-blink) and still read as HAL 9000 / a Cylon rather than R2D2,
  because R2 is predominantly *white* with navy accents and its main eye is
  *black*, not a glowing red lamp. A big red glow on black is the wrong
  silhouette no matter how well the details are executed. What fixed it was
  inverting the ground — light plate filling the frame, navy panels, black
  lens — not tuning the red. The whole `drawEyeRadialGradient` /
  `MI2MO2_PANEL_*` / pupil-offset machinery from that version was deleted;
  don't reintroduce a glowing eye here.
  The consequence for animation is that **the lens carries no expression at
  all**: it's a fixed black disc that never blinks, squints, moves or
  changes color. Two things stand in for that, both mirroring how the real
  droid works — its eye is a static piece of glass and all its emoting
  happens in the logic panels:
  (a) **look-around slides a white glint across the lens**
  (`MI2MO2_GLINT_*`) instead of moving the eye — on featureless black
  glass a travelling reflection is the only available cue that the lens
  turned. The glint rests up-and-left of center, which is why
  `Personality.cpp` excludes exactly one look direction in this theme (see
  `LOOK_DIRECTIONS_NO_UP_LEFT` there); and
  (b) **the logic display — the small round lamp right of the lens — carries
  every expression**, including the ordinary blink (it switches off and
  back on, since a black lens has no light to close). `Face::render`
  resolves its color and brightness in one small block before the dispatch:
  THINKING → `mi2Mo2ThinkingDim` (an irregular stutter, two nested time
  scales — a coarse "burst" hashes the flicker rate, each slot within it
  hashes to lit/nearly-out/half-lit — so it never settles into a mechanical
  rhythm), FAILED → `mi2Mo2ErrorDim` (three flashes, then holds *lit*, not
  dark: FAILED outlives the flashes by `FACE_OVERRIDE_DURATION_MS` and a
  dark lamp for the remainder would read as "asleep"), FINISHED → green,
  SLEEPING → off. FAILED is the one bounded animation in this whole
  codebase — everything else (glitch, Matrix rain, coffee steam) loops off
  `nowMs` forever and needs no start time — which is why `FaceState` grew
  `expressionStartedMs` (set by `Personality::update` when
  `_renderExpression` changes) purely for it.
  MI2MO2 also opts out of the whole-frame `DimmingDisplay` on SLEEPING
  (there, sleeping is specifically the lamp going out with the plate,
  badges and message all at full strength), suppresses SLEEPING's "Z Z Z"
  and COFFEE's cup (COFFEE there is message-only), and splits what every
  other theme keeps as one color: weather/clock badges are navy (fixed
  info printed on the plate; the light plate washes out CLASSIC's teal),
  while the corner icons (music/play/book/gamepad/coffee) are the lamp's
  red (transient status lights) — hence `Face::render`'s separate
  `eyeR/G/B` vs `iconR/G/B`. The message box stays CLASSIC's dark grey with
  white text: an earlier near-white bubble blended straight into the light
  plate.
  **Aurebesh translation effect** (`drawWrappedMessageMi2Mo2`): each
  character is drawn individually rather than a line at a time, because
  each sits at a different point in its own reveal age — derived from
  `FaceState::messageTypingStartedMs` plus its index times
  `TYPING_CHAR_INTERVAL_MS` (which moved to `Face.h` so `Personality` and
  `Face` can't drift on it). For `AUREBESH_HOLD_MS` after being revealed a
  character draws in Aurebesh *red*, then flips to Latin *white* — font and
  color switch together on that same per-character clock, so the line shows
  a visible "decoding wavefront": already-resolved white at the start,
  still-alien red at the end. The Aurebesh red is deliberately lighter than
  the lamp's own red — at 5x7 against the dark box the deeper red loses the
  thin strokes. Fonts were generated from a real `Aurebesh.otf`, not
  hand-drawn, by rasterizing each glyph large and reducing each cell to the
  *darkest* source pixel rather than a smooth resize (a plain resize
  anti-aliases thin diagonals like the digit `7` away to nothing before
  thresholding). Both sides carry their own copy in their own format —
  `AurebeshFont.cs` (5x7 column-major, Simulator) and
  `AurebeshGFXFont.h` (Adafruit `GFXfont`, firmware) — and both cover only
  `A-Z`/`0-9`, falling back to Latin for anything else. `TextFont`
  (`IDisplay.h` / `IDisplay.cs`, plus a token on the wire's `TEXT` line)
  is what carries the choice; `ST7735PhysicalDisplay::drawText` switches
  fonts per character and shifts its cursor down by the glyph height while
  Aurebesh is active, since a custom `GFXfont` draws from the text
  *baseline* while the built-in font draws from the top-left corner —
  getting that wrong misaligns Aurebesh against the Latin characters beside
  it.
  **`Theme::MI84`** (Sender's Tema card, "MiMo-84" entry, `THEME MI84`) is a
  1984 amber-CRT terminal, and it is the mirror image of MI2MO2's move:
  where that theme replaces the *face*, this one replaces the *frame*. The
  top two thirds become fixed terminal chrome — a `MIMO SYSTEM v2.6` header,
  a status row, two rules and a tab bar — and MATRIX's own small
  bottom-pinned eyes sit underneath, still expression-shaped. Everything is
  drawn in one amber (`MI84_INK_*`, 255/176/0) on black, with a darker amber
  (`MI84_DIM_*`) for chrome only: rules, labels, unlit bar cells.
  It deliberately **does not** use `RecoloringDisplay` the way MATRIX does.
  That decorator flattens every non-black color to a single value, and this
  theme needs those two levels, so colors are threaded through explicitly,
  the way MI2MO2 already does. The one thing that trips over this: the
  corner icons take `iconR/G/B`, which defaulted to CLASSIC's teal — MI84
  suppresses every icon except COFFEE's cup, so the cup came out as the
  single non-amber object on an otherwise monochrome screen. A real bug,
  fixed once, and the reason `iconR/G/B` now has an MI84 arm of its own
  rather than sharing `eyeR/G/B`.
  The log and the tabs are **MATRIX's, reused whole** — same
  `FaceState::logLines`, same `LogTab`, same `Personality::pushLogLine`,
  same `drawMatrixLog` (which grew position/color parameters for this; every
  MATRIX call site passes exactly the constants it used to read directly, so
  that theme's output is byte-identical). This is what made the SDD's
  "IA activity must always win the tab" requirement cost *zero* new logic:
  `Personality::currentState` already derives the tab from what's rendering,
  and foreground beating background in `resolveExpression` already produces
  exactly that priority. Anything else would have been a second copy of a
  rule that already existed.
  Where it does diverge from MATRIX:
  **Hora/Clima are text, not badges** — both readings share one row
  (`TIME 17:42  RAIN 18C`, 20 chars at `CHAR_ADVANCE_PX`) and
  `drawWeatherBadge`/`drawClockBadge` are suppressed, since drawing the
  pictograms as well would print the same information twice in a strip the
  header already occupies. The condition prints as the *wire protocol's own
  `WEATHER` token* (`CLEAR`/`CLOUDY`/`RAIN`/...) rather than a prettier
  second vocabulary that could drift from what's actually being sent. No
  degree symbol — `Font5x7` has none, and `drawWeatherBadge` already prints
  a bare `18C` for the same reason.
  **THINKING doesn't use the glitch eyes.** Here it's carried by the eyes
  flickering (`mi2Mo2ThinkingDim`, reused as-is — it was never really
  MI2MO2-specific, just the first place that needed an irregular stutter)
  plus a `>THINKING_` prompt line that types itself out, erases and repeats,
  driven purely off `nowMs` like every other looping effect here. That
  prompt takes **only the last content row**, with the log still scrolling
  above it — which is the whole reason the `PreToolUse` tool labels
  ("Executando comando...") stay visible instead of being hidden behind the
  animation, as they would have been had it claimed the whole region.
  **Both sleep states type a prompt of their own**, `>Z..Z..Z...Z.._`, off
  the very same `drawMi84TypedPrompt` the THINKING row uses — it takes the
  text and the pacing as parameters, so the two differ only in those. The
  sleep pacing is deliberately about twice as slow (`MI84_SLEEP_CHAR_MS`
  210 vs 110, longer hold and blank): at the AI's brisk rate the same
  characters read as busy rather than drowsy. The uneven run of dots lives
  in the string itself rather than in variable timing, so pauses of
  different lengths fall out for free and this stays on one code path.
  `SLEEPING` (the 10-minute idle deep sleep) puts it on the AI tab's last
  row exactly as THINKING does, with the log still scrolling above and the
  whole frame already dimmed by the `DimmingDisplay`. `SLEEPY` (the
  clock-driven bedtime nudge) instead swaps the content area for a
  `SYSTEM NOTICE` panel and puts the prompt on its bottom row; the phrase
  above it is `Personality`'s own PT-BR `BEDTIME_MESSAGES` pick, which
  already reaches the log via `pushLogLine`, so nothing extra was plumbed
  for it. That panel used to carry a third chrome line, `RECOMMENDATION:`,
  which was dropped to free the row the prompt now occupies — it was
  spending one of five scarce content rows restating what the phrase
  directly below it already said.
  **COFFEE** keeps the header/status/tabs and yields only the content area
  to the cup — the same "skip that region while this expression is up, no
  state to track" move MATRIX already makes with its log.
  Chrome is English, content is Portuguese, deliberately: `NOW PLAYING` and
  `SYSTEM MONITOR` are the machine's own labels, while everything MiMo
  actually *says* stays in the language it already speaks.
  **The boot sequence** (`drawMi84Boot`) is the theme's other bounded
  animation — `MIMO-84 BIOS`, four POST lines appearing one at a time,
  `SYSTEM READY`, then a centered banner, ~4s in total, after which the eyes
  strike like an old lamp (`mi84LampLevel`: dark, two failed strikes that
  flare and die, then a climb to full — a breakpoint table rather than a
  curve, because the misfires *are* the effect and no easing function
  expresses "it nearly caught, then dropped"). Being bounded, it needs a
  time anchor the way MI2MO2's three-flash FAILED does, hence
  `FaceState::themeStartedMs`. It plays on **every** `THEME MI84` command
  rather than once per power-on, and that is the mechanism, not a
  side effect: Core has no "a PC app just connected" signal, but
  `Brobot.Sender` sends `THEME` first on a fresh link and re-sends it on
  every reconnect, so the command *is* that signal — the only way "boot when
  MiMo reaches the PC" was expressible at this layer.
  **Sounds are unchanged** — MI84 plays the same R2D2-flavored cues as every
  other theme. That's an explicit decision to leave `Buzzer` alone (it takes
  no `Theme` today), not an oversight; giving MI84 its own square-wave beeps
  would be a `Theme` parameter on `playForExpression` plus one more
  `SoundSegment` table, nothing structural.
  **Game Mode in the themes with no log** (`drawStatsMessage`): CLASSIC and
  MI2MO2 have no console log to put stats in (MATRIX and MI84 both do, and
  each renders them in its own MONITOR tab — as text rows and as bar meters
  respectively), so while PLAYING is on screen
  the message box grows (`drawMessageBox` takes a line count for this) and
  becomes the readout — one reading per row, matching how the MONITOR tab
  lists them, with the game's name wrapped above.
  The two themes get different heights for a physical reason, not a stylistic
  one. CLASSIC takes 5 rows because it can move its eyes out of the way:
  `GAME_EYE_SIZE`/`GAME_EYE_Y` shrink them to 26px and pin them at y=18, just
  under the weather/clock strip — the same "this expression reorganizes the
  frame" move COFFEE already makes for its cup, and only while PLAYING is
  rendering. MI2MO2 gets 4 rows and keeps its plate untouched, because its
  lens is a fixed disc ending at y=77 and a 5-row box would start at y=71 and
  cover the bottom of R2's eye; 4 rows start at 80 and clear it. That's the
  most this theme can grow without redrawing the plate, which is why a long
  game name still truncates there and doesn't in CLASSIC.
  The three stat rows are anchored to the bottom of whichever box, so the
  numbers hold still while the name above them wraps to different heights.
  Drawn directly rather than pushed
  through the message system on purpose: a message there types in one
  character at a time and then expires, which is right for something said
  once and wrong for a panel replaced every two seconds — it would retype
  itself on every `STATS`, and the numbers would never sit still long enough
  to read. Bypassing it is also what keeps the panel open with no expiry to
  fight. The game's name still arrives as the background tier's own message,
  so nothing extra was plumbed through for it; it's simply drawn rather than
  typed, which does mean it shows the typewriter's partial text for the first
  second after a game starts.
- **`Personality.cpp`**: blink and look-around use eased (smoothstep) transitions
  spread over enough frames to look smooth at ~20fps, not instant jumps. Look-around
  picks randomly from 8 directions (incl. diagonals) and swings close to the
  screen edges — except while `THEME MATRIX` or `THEME MI84` is active
  (both pin the eyes to the bottom), where the 3 downward
  ones (down, down-left, down-right; `LOOK_DIRECTIONS_NO_DOWN`, indices into
  the same `LOOK_DIRECTIONS` table so both pools stay in sync with
  `LOOK_OFFSET_X_PX`/`Y_PX`) are excluded from the pool — both pin the
  eyes near the bottom edge (see `MATRIX_EYE_BOTTOM_MARGIN` in `Face.cpp`,
  which MI84 reuses rather than defining its own), leaving too little
  clearance to look further down without crowding or crossing that edge. `THEME MI2MO2` filters the same pool for a different
  reason (`LOOK_DIRECTIONS_NO_UP_LEFT`): it doesn't move an eye at all, it
  slides a reflection across a fixed lens (see `Theme::MI2MO2` above), and
  up-left is the single direction whose offset carries that glint off the
  edge of the glass — up alone and left alone both stay inside it, so only
  the one diagonal is dropped rather than a whole side. Falls asleep after `SLEEP_TIMEOUT_MS` (10 min) idle; blink/look
  pause entirely while asleep (also while FAILED/READING/THINKING hold still, or
  MUSIC dances — those drive their own motion off `nowMs` in Face.cpp instead;
  PLAYING is *not* in that hold-still list, same as WATCHING — the gamepad icon
  bobs on its own, but the eyes keep blinking/looking around normally, since
  "playing a game" isn't a motionless state the way sleeping or reading is).
  `_expression`/`_gameExpression`/`_mediaExpression`, plus the notification
  tier above them all, are independent priority tiers
  (`Personality::Tier`), each with its own `TypedMessage` (typewriter state
  + expiry), not one shared expression/message pair — this is what lets an
  AI message interrupt MUSIC/WATCHING/PLAYING without losing track of it: a
  real bug, fixed once (previously all four of MUSIC/WATCHING/THINKING/
  PLAYING lived in the same sticky `_expression`, so whichever the AI's
  THINKING/READING/FINISHED last overwrote never came back once its own
  10s-ish window expired, even though the media/game was still going).
  `MUSIC`/`WATCHING` live in `_mediaExpression` and `PLAYING` in
  `_gameExpression` (both sticky by construction — nothing times either out,
  see `resolveExpression`), set/cleared by `FACE MUSIC|WATCHING`/`FACE
  IDLE_MEDIA` and `FACE PLAYING`/`FACE IDLE_GAME`, never `_expression`. The
  two were one shared slot until the notification work split them; a game
  now outranks media rather than whichever arrived last winning. `THINKING` is the one remaining sticky *foreground*
  expression (`_expression == THINKING` short-circuits
  `resolveExpression`); every other foreground expression times out via
  `_expressionOverrideUntil` (`FACE_OVERRIDE_DURATION_MS`, 4s) same as
  before, and `FACE NEUTRAL` collapses that window to `now` (not `+4s`) so a
  stored background can take over immediately instead of sitting through a
  redundant "showing NEUTRAL" wait. Once foreground's override lapses,
  `resolveExpression` falls back to `_gameExpression`, then
  `_mediaExpression` (whichever is set) before
  falling further to idle/SLEEPING/NEUTRAL — that fallback, plus each tier
  keeping its own message text, is what makes MUSIC/WATCHING/PLAYING (and
  their "now playing"/"Jogando X" label) reappear on their own once an
  interrupting AI message goes away, instead of the two racing to overwrite
  a single shared expression/message pair.
  A background `FACE` command received while foreground is active only
  updates the stored background state (`onFaceCommand`) — it doesn't touch
  what's currently rendering, so starting music/a game mid-AI-message keeps
  it hidden until the AI message clears, per the same fallback. `MSG` with no
  preceding `FACE` in the same "turn" (a `Notification` hook event, which
  intentionally sends no `FACE` — see `OnAiThoughtReceived`) routes to
  whichever tier the *previous* `FACE` command belonged to (`_lastCommandTier`)
  — and `onMessageCommand` also stretches `_expressionOverrideUntil` far
  enough for that message's own typing+hold time, so a bare Notification (no
  `FACE` at all) still counts as high-priority foreground content that
  interrupts background instead of being silently absorbed by it.
  Each tier's message is sticky the same way it always was — MUSIC/
  WATCHING/PLAYING's `TypedMessage` never auto-expires (`persistDurationMs
  == 0`) since it's a "now playing"-style label, and THINKING's foreground
  message doesn't either while `_expression == THINKING`; every other
  foreground message still auto-clears `MESSAGE_DURATION_MS` (10s) after
  typing finishes. Messages "type" in character-by-character
  (`TYPING_CHAR_INTERVAL_MS`) regardless of which tier is currently
  rendered — a background message keeps typing in while hidden behind an
  active foreground one, so it's already sitting there fully revealed the
  instant it becomes visible again, no retyping. `MESSAGE_CAPACITY` is 255
  chars, per tier.
  `onThemeCommand` stamps `_themeChangedAt` on **every** `THEME` command, not
  only on an actual change of value, and that's what MI84's boot sequence
  hangs off (`FaceState::themeStartedMs`). Core has no "a PC app just
  connected" signal of its own, but `Brobot.Sender` sends `THEME` as the
  first thing over a fresh link and re-sends it on every reconnect (see
  `UpdateConnectionStatus`), so the command *is* that signal — which is the
  only reason "play the boot sequence when MiMo reaches the PC" is
  expressible down here at all. The other themes never read the field.
  `onWeatherCommand`/`onTimeCommand` deliberately never touch `_lastInteractionAt` —
  they're passive background telemetry from whichever PC app is connected, not user
  interaction, so a Sender pushing `TIME` every minute must not keep Brobot awake
  forever. A short **boot animation** plays once per `begin()` (i.e. once per
  power-on/reset, anchored by `_bootStartedAt`): the eyes drop in from just
  above their resting position with an overshoot/bounce (`easeOutBack`, ~700ms),
  then settle with a quick decaying side-to-side wobble (~500ms) — "falling and
  reorganizing" rather than popping straight into view. Deliberately no
  `sinf`/`powf`/libm calls (`easeOutBack`/`triangleWave` are hand-written
  polynomial/piecewise functions) since this file also has to compile against
  `BrobotCore/native`'s minimal `Arduino.h` shim, which doesn't wire up
  `<math.h>` — same reasoning as `smoothstep` above.
  **`SLEEPY`** is a second, lighter sleep state, entirely clock-driven
  (`isBedtimeHour`, 22h-6h) rather than idle-driven like `SLEEPING` — it wins
  over plain `NEUTRAL` in `resolveExpression`'s fallback, but the 10-minute
  idle `SLEEPING` check still takes priority over it (once actually asleep,
  the drowsy nudge stops making sense). Visually it's `NEUTRAL`'s eye shape
  just slightly squinted (`shapeFor`, no new Face.cpp render branch needed —
  it falls through to the plain `drawEye` path, no icon, no "Z Z Z"), but the
  blink itself runs on a much longer `BLINK_DURATION_SLEEPY_MS` (900ms vs the
  normal 280ms) so it reads as an obviously heavy-lidded blink rather than
  the quick NEUTRAL one — `updateBlink` picks the duration off
  `_renderExpression` each call. Unlike `SLEEPING`/`FAILED`/`READING`/
  `THINKING`/`MUSIC`, `SLEEPY` is *not* in `update()`'s hold-still list, so
  ordinary blink/look-around keep running (just with that slower blink).
  While it's bedtime hours, `Personality` also autonomously picks one of 10
  hardcoded PT-BR "go to sleep" phrases (`BEDTIME_MESSAGES`) into its own
  `_bedtimeMessage` (a third `TypedMessage`, alongside foreground/
  background — not either of those tiers since no PC app is commanding this)
  the moment bedtime starts, then again every `BEDTIME_MESSAGE_INTERVAL_MS`
  (30 min) for as long as it stays bedtime, tracked independently of
  whatever's actually rendering so the schedule doesn't drift if AI activity
  or media is occupying the screen when a 30-minute mark passes. Each pick
  auto-clears `MESSAGE_DURATION_MS` (10s) after typing finishes, same as a
  normal foreground message, instead of sitting on screen for the full 30
  minutes until the next one replaces it.
  `currentState()` only shows `_bedtimeMessage` while `_renderExpression ==
  SLEEPY`. All of this is driven off `_timeText` — the same field `TIME`
  already populates for the weather badge's day/night icon (see
  `isNightHour` in `Face.cpp`) — since Core still has no RTC of its own;
  `FACE SLEEPY` is also accepted as an explicit command (`parseExpression`)
  for testing without waiting on the clock.
- **`WifiSetup.cpp`** (ESP32 only, see `WifiSetup.h`): `connectOrStartPortal()`
  tries saved credentials (`Preferences`/NVS) first, falling back to the
  "MiMo-Setup" config-portal AP on failure/no-saved-creds — see Build & run
  above for the user-facing flow. Takes an optional per-tick callback so
  `main.cpp` can keep the physical display alive (drawing a static
  `FaceState` directly) while the portal's blocking `WebServer::handleClient()`
  loop runs, without `WifiSetup.cpp` itself needing to know about
  `Face`/`Personality`/`IDisplay` at all.
- **`Buzzer.cpp`** (`BUZZER_PIN` = GPIO0 on the ESP32-C3 SuperMini — the one
  pin left over from `Config.h`'s already-vetted GPIO0/1/3/4/6/10 safe set
  once the display claims the rest): R2D2-style beeps via `tone()`/
  `noTone()`, adapted from an early breadboard test sketch (`buzzer_test.ino`,
  outside this repo) whose `sweep()`/`trill()` helpers stepped through
  frequencies with `delay()` between each one — fine standalone, but that
  would freeze the ~60fps render loop and stop `Protocol::poll` from reading
  incoming commands for as long as a sound played, so this is a rewrite of
  the same sound shapes as a small non-blocking state machine instead, driven
  purely off `nowMs` (`Buzzer::update`, called every `loop()` iteration same
  as `Personality::update`) — same "no `delay()`" approach every other timed
  effect in this codebase already uses. `update()` steps through a single
  current `SoundSegment` (`SWEEP`/`TRILL`/`TONE`/`SILENCE`), only
  re-triggering `tone()` when a sub-step actually changes (tracked via
  `_lastStepIndex`) rather than on every call, and — once that segment's
  `durationMs` elapses — asks a `SegmentSupplier` function pointer for the
  next one; `update()`'s own stepping logic doesn't care which supplier is
  feeding it, so it's written exactly once. `main.cpp`'s
  `renderPersonalityFrame` compares `state.expression` against a
  `lastSoundExpression` it tracks across frames and calls
  `Buzzer::playForExpression` only on an actual *change* — calling it every
  frame while the expression stays the same would keep restarting a
  still-playing cue from its first segment, so a one-shot cue would never
  finish and THINKING's chatter (see below) would never be allowed to
  actually vary.

  Two suppliers exist, matching two different fidelity needs against the
  original test sketch: `nextFixedSegment` walks a plain declarative
  `SoundSegment[]` table (the same shape `BEDTIME_MESSAGES`/`PausaMessages`
  already use elsewhere) and returns false once it's exhausted, ending the
  cue — used for `FINISHED` (a triumphant rising sweep, adapted from
  `sayTerminei()`, matching its own "Terminei!" message), `FAILED` (three
  quick high trills, adapted from `sayAtencao()`, an "uh-oh"), and
  `READING` (an up-then-down curious chirp, adapted from `sayOlhaAqui()`) —
  all one-shot, playing once when that expression starts. `THINKING` uses
  `nextChatterSegment` instead: the original `sayPensando()` picked a fresh
  random chirp (an up-sweep, a down-sweep, a trill, or a bare blip, each
  with randomized frequencies/duration) every time through a 3-second
  `while`/`delay` loop, and a static table couldn't reproduce that
  "muttering to itself" quality — an early version of this feature tried a
  fixed, identically-repeating motif instead, and it read as far too
  mechanical/repetitive compared to the original, a real regression, fixed
  once. `nextChatterSegment` instead directly ports `sayPensando()`'s same
  random branch-picking (`_chatterInGap` alternates a chirp with a
  `random(100,250)`ms silent gap between chirps, same shape the original's
  trailing `delay()` had) and, unlike `nextFixedSegment`, never returns
  false — it keeps generating new random chirps for as long as THINKING
  stays the active expression, how ever long the AI actually takes, rather
  than a fixed-length clip. Every expression without a mapped cue falls to
  `playForExpression`'s `default:`, which calls `stop()`; this matters
  specifically for THINKING's endless chatter, since without it a switch
  away from THINKING would leave the muttering going forever, with nothing
  left to ever tell it to stop. The original sketch's `sayAlarme()` (a 20s
  siren) was deliberately left unported — nothing in Core's current
  expression vocabulary represents a sustained alarm, and 20s is far too
  long to fire automatically off a single expression change; easy to wire
  up later (e.g. a future `WEATHER STORM` alert) if wanted.
  Firmware-only: unlike `Face`/`Personality`/`Protocol`, `Buzzer.cpp` isn't
  in `native/build.ps1`'s explicit source list, so the native dev build
  (no speaker on a dev PC) simply never compiles or references it — same
  "PlatformIO-only" treatment `ST7735PhysicalDisplay`/`WifiSetup` already get.
- **`Protocol.cpp`**: byte-at-a-time line reader. If more than `LINE_STALE_TIMEOUT_MS`
  (300ms) passes mid-line, the partial buffer is discarded before continuing —
  otherwise a stray disconnect/noise byte can silently corrupt the next real command.
  `dispatch` takes the `Stream` (rather than `poll` keeping it to itself) for
  exactly one reason: `PING` answers `MIMO 1` back down it. That's the only
  command Core replies to and the only one routed to neither `Personality` nor
  `DeviceSettings` — it says nothing about Brobot, only that this is a Brobot,
  which is precisely what `MimoDiscovery`'s network sweep needs to hear (see
  Brobot.Connection internals). The reply's trailing number is a protocol
  revision, there so a future PC app can tell an old board from a new one
  without a second round trip; bump it only for changes a client would
  actually have to branch on.
- **`native/src/TcpBroadcastStream.cpp`**: the native build's `Stream` — listens on
  a TCP port and accepts multiple simultaneous clients (non-blocking `accept()`,
  polled once per main-loop iteration), broadcasting every draw command to all of
  them and merging control-command bytes from whichever client has data available
  into the one logical stream `Protocol::poll` reads. A per-client `SO_SNDTIMEO`
  (200ms) keeps one stalled/non-draining client (e.g. a sender app that stopped
  reading) from blocking draw delivery to everyone else — it just gets dropped.
- **`FRAME_INTERVAL_MS`** (`Config.h`) currently 16ms/~60fps — set for smooth motion
  during TCP-only dev via `BrobotCore/native`. This is **several times over** the
  115200 baud link's ~11.5 KB/s budget (roughly: commands/frame × ~25 bytes × fps)
  — but that budget only matters for `VSCREEN=1` builds, where draw commands
  travel over the same `Serial`/UART the baud rate applies to. It does *not*
  apply to `esp32dev_physical` (the hardware actually in hand): draws go
  straight over SPI to the physical panel, and the WiFi control channel
  carries only tiny `FACE`/`MSG` lines, not full frames — no baud budget in
  the loop at all there. If `VSCREEN=1` is ever flashed to *real* Serial
  hardware (the Uno, or an ESP32 over an actual slow UART instead of its
  native USB), **restore 50ms/~20fps first** (or raise `SERIAL_BAUD_RATE`),
  or frames will lag/garble.

## WPF app internals (Brobot.Display.Simulator)

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
  to 2px; the terminal punctuation went in the same way, for MI84). Adding
  those last ones also fixed a bug that predated MI84 and only ever showed up
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
  `Brobot.Connection/BrobotConnection.cs` (see below).
- **`MainWindow`**: scale selector includes 1x–8x. Default scale is 3x, sized for
  the 160x128 landscape frame — a too-large scale can make the display overflow
  the fixed-size window (it doesn't auto-resize).
- **`DisplayTestPattern.cs`**: local demo shapes only, unrelated to Brobot — exists
  purely to exercise the renderer.

## Brobot.Connection internals

- **`BrobotConnection.cs`**: `ConnectSerial` opens the port *and* reads on a
  dedicated background thread — never the caller's — not
  `SerialPort.DataReceived` (known to silently stop firing on Windows).
  `SerialPort.Open()` itself used to run directly on the calling (WPF button
  click) thread; against the ESP32-C3 SuperMini's native USB-CDC port
  specifically (unlike a real UART bridge chip such as the Uno's), `Open()`
  could hang indefinitely, freezing the whole app on connect — this was a
  real bug, fixed once by moving `Open()` onto the background thread too.
  A second, subtler version of the same class of bug: `IsConnected` used to
  read `_port.IsOpen` directly, and reading a `SerialPort` property from the
  UI thread while the background thread had a pending `ReadLine()` on the
  *same* `SerialPort` also hung against this port — fixed by tracking
  connectedness in a separate `volatile bool _serialOpen` instead, so
  `IsConnected`/`SendCommand` never touch `_port`'s properties cross-thread.
  Given both bugs, `Brobot.Sender` dropped Serial from its own UI entirely
  (WiFi-only now — see Brobot.Sender internals below); `Brobot.Display.Simulator`
  still uses `ConnectSerial` for a real Arduino/ESP32 over COM, and benefits
  from both fixes.
  `ConnectTcp` retries every 500ms via `TcpClient.ConnectAsync`
  bounded by a 500ms `Task.Wait` (not a plain blocking `Connect()`, so `Disconnect()`
  is noticed promptly instead of blocking on the OS's much longer default TCP
  connect timeout) — this is what lets Core and the app start in either order, and
  lets the app recover on its own if Core restarts mid-session.
- **`MimoDiscovery.cs`**: finds MiMo's current IP by sweeping the local
  network, because that IP is not stable — it comes from the router's DHCP
  server, so power-cycling MiMo (or the router) can move it, and the address
  saved in Sender's Conexão card then points at nothing. It reads the PC's own
  adapters (`NetworkInterface`) for their address+netmask rather than assuming
  a `192.168.x.0/24`, probes every host on those subnets, and asks each host
  that accepts a connection to identify itself with `PING` (see PROTOCOL.md).
  Only a host that answers `MIMO` is adopted.
  **That confirmation step is the whole point, not belt-and-braces**: port 5555
  isn't reserved for this project (Android's ADB-over-network uses it, among
  others), and the network this was built on turned out to have an unrelated
  device answering on it — a "first open port wins" sweep would have adopted
  that device as MiMo and quietly sent it `FACE`/`MSG` from then on. The single
  exception is the address that was *already in use*: if it still accepts
  connections but won't answer `PING`, it's taken as a board running firmware
  from before `PING` existed. An unidentified host at any *other* address is
  rejected outright, which does mean **relocating a moved MiMo requires the
  `PING` firmware** — without it a sweep can only reconfirm an address that
  already worked.
  Probes run 64-at-a-time behind a `SemaphoreSlim`: a full subnet is ~254
  hosts and an address with nothing at it costs the entire connect timeout
  (ARP silence, not a fast RST), so sequential probing would take minutes
  instead of the ~3.2s a full sweep actually takes. They're also launched
  `ProbeLaunchStagger` (10ms) apart rather than all at once, and that spacing
  is about *legibility*, not load: launched together, every probe against an
  empty address times out at the same instant, so the sweep finishes in slabs
  and the progress count measured 0, 63, 64, 127, 128, 191, 192, 253 — it
  lurched rather than climbed. Spacing the starts spaces the finishes, which
  turned the same measurement into 6, 41, 75, 110, 144, 179, 214, 248, 253.
  It costs ~700ms on a full sweep (the last probe still serves out its own
  timeout) and nothing on the common case, since probe 0 is unstaggered. A
  confirmed hit returns
  immediately and cancels the rest, so the common case — MiMo still where it
  was, probed first because the previous address is always candidate 0 — comes
  back in well under 100ms. Adapters without a default gateway are skipped
  (that's what separates the real WiFi adapter from the Hyper-V/WSL/VirtualBox/
  Docker pile on a dev machine), as is any subnet larger than
  `MaxHostsPerSubnet` (512) so a stray /16 like APIPA's can't turn into tens of
  thousands of probes. Neither the sweep's `CancellationTokenSource` nor its
  semaphore is disposed via `using` — probes are still in flight when an early
  confirmed hit returns, and disposing either out from under them throws
  `ObjectDisposedException` inside tasks nobody is awaiting; the `finally`
  cancels and awaits them instead.
- Incoming lines are batched into frames (ended by a `PRESENT` line) and delivered
  via one **synchronous** `Dispatcher.Invoke` per frame, not `BeginInvoke` and not
  one `Invoke` per line: `BeginInvoke` would let a backlog build up silently if the
  UI thread ever fell behind, so a fresh `MSG` meant to interrupt what's showing
  could end up stuck behind stale frames — this was a real bug, fixed once. Batching
  by frame instead of by line cut UI-thread crossings by ~20-30x, which mattered
  once Core's frame rate was raised for TCP dev use (see `FRAME_INTERVAL_MS` below).
- The TCP `StreamWriter` must use a BOM-less UTF8 encoding — the default one emits
  a 3-byte preamble on the first write, which silently corrupted the first command
  ever sent (its bytes no longer matched "FACE"/"MSG", so `Protocol::dispatch` just
  ignored it) — this was a real bug, fixed once.

## Brobot.Sender internals

This is the app for whoever actually assembled a Brobot — not a dev tool, and
user-facing branded **"MiMo"** throughout (window titles, tray tooltip, every
string an end user sees) even though the code/project/namespace keep the
Brobot name everywhere. It runs in the system tray and, when opened, shows
a checklist of what to send MiMo — **no separate settings window**: there
used to be one (`SettingsWindow`, COM-port picker + TCP host/port +
theme combo), deleted entirely once Serial was dropped from this app (see
Brobot.Connection internals above) — the Conexão card (IP:port field +
Conectar/Desconectar) is now just another card in the main view, and the
theme combo already had its own duplicate card there too. Checking a box
turns on a background watcher that decides *when* to send something; it
never decides *how it should look* — that's still 100% Core's call per the
one rule at the top of this file.

The main-window layout (MiMo wordmark, six feature cards — Conexão, Hora,
Clima, Atividade da IA, Mídia, Jogos — a Tema card, an info card, a "Salvar
configurações" button, warm cream/tan palette) follows a supplied design
reference closely — see `MainWindow.xaml`'s `Window.Resources` for the
color brushes and the custom `CheckBox`/`ComboBox`/`Button`/`TextBox`
control templates (WPF's stock chrome doesn't look anything like flat
rounded cards, so all four are fully retemplated rather than just
re-colored). The checkmark `Path` inside `MimoCheckBoxStyle` needs explicit `HorizontalAlignment`/
`VerticalAlignment="Center"` — its `Data` uses absolute coordinates for a
small checkmark shape, and a `Path` with `Stretch="None"` (the default)
draws that geometry anchored to its layout slot's top-left corner, not
centered in it, so without those two setters the check sits visibly
off-center in the 28x28 box — this was a real bug, fixed once.

- **Conexão card**: a single `TextBox` (`ConnectionAddressTextBox`, "IP:porta"
  in one field, e.g. `192.168.1.50:5555` — parsed by splitting on the *last*
  `:` so a literal IPv6 address wouldn't break it) plus a Conectar/Desconectar
  `Button`. `UpdateConnectionStatus()` (a 200ms `DispatcherTimer` tick) is the
  **single source of truth** for both the status text and the button's own
  label/enabled-state — it used to only touch the status text, while the
  button's content was set optimistically by whichever code path last called
  Connect/Disconnect and never corrected afterward; if the TCP connection
  dropped on its own (Core unreachable, network blip), the button stayed
  stuck reading "Desconectar" while `IsConnected` was already false, so
  clicking it hit the *reconnect* branch instead of actually disconnecting
  anything — read as "the button doesn't work". This was a real bug, fixed
  once. The address is a **readout, not a field** — a `TextBlock`, not a
  `TextBox`: MiMo's IP is discovered, never typed, and the app's own
  `_coreHost`/`_corePort` are the source of truth the card displays. It used
  to be the other way round, with the text in an editable box *being* the
  truth, which is precisely what broke every time DHCP moved MiMo — the
  address was only ever as correct as whatever someone last typed. (With the
  field gone, `TryParseAddress` and its "Endereço inválido" message went with
  it; there's no longer any input to be invalid.)
  `RestoreSettings()` loads the saved host:port for display, but
  **deliberately does not connect to it** — every launch starts with a fresh
  sweep (`StartNetworkSweep(trustPreviousAddress: false)`) instead. Between
  one run and the next the app may have been closed for days: long enough for
  MiMo to have been given a different address, and for DHCP to have handed its
  old one to some other device — where `MimoDiscovery`'s
  "unconfirmed-but-listening at the address already in use" fallback would
  adopt a stranger. Asking the network beats trusting a note from last time,
  and it costs one ~3.2s sweep at startup. That's the entire difference
  between the two `trustPreviousAddress` cases: a mid-session recovery *does*
  trust the address, because it was demonstrably reaching MiMo moments ago.
  Because a saved address goes stale on its own whenever DHCP moves MiMo,
  `UpdateConnectionStatus` also drives an automatic network sweep
  (`TryStartNetworkSweep` → `MimoDiscovery`, see Brobot.Connection internals):
  once `ConnectTcp` has been getting nowhere for `SweepAfterFailingFor` (8s),
  the address itself is treated as the suspect and the network is searched for
  MiMo; whatever is found replaces the field's text, reconnects, and is written
  **straight to the settings file without waiting for "Salvar configurações"**
  (`PersistDiscoveredAddress`) — unlike every other setting there, MiMo's
  address isn't a preference someone chose, it's a fact about where the device
  currently is, and not saving it would mean re-sweeping on every launch.
  The 8s delay is what keeps an ordinary blip (MiMo still booting, WiFi
  reassociating) from triggering a sweep that `ConnectTcp`'s own 500ms retry
  loop was about to make unnecessary, and `SweepCooldown` (30s) is what keeps
  MiMo simply being *switched off* — indistinguishable from MiMo having moved,
  without sweeping — from sweeping back-to-back forever. This is polled rather
  than event-driven because `BrobotConnection` has no "gave up" event to
  subscribe to: `ConnectTcp` retries forever by design, so "how long has this
  been failing" is something only the status poll can notice.
  The button's label tracks **connected and nothing else**: "Desconectar" only
  when there is a live connection, "Conectar" in every other state. It used to
  read "Desconectar" while merely connecting or sweeping as well, on the
  reasoning that a click could call those off — but `ConnectTcp` retries
  forever, so a search that found nothing left the button reading "Desconectar"
  indefinitely with nothing connected to disconnect from. This was a real bug,
  fixed once. It's disabled only while a sweep is actually in flight, the one
  state where a second click would stack a duplicate search.
  Clicking "Conectar" means **try now**, and what that entails depends on the
  state: with a known address and nothing currently trying it (the
  reconnect-after-Desconectar path) it's one direct `ConnectTcp`; with nothing
  known, or with a known address already being retried fruitlessly, it's an
  immediate sweep that skips the delay/cooldown `TryStartNetworkSweep` would
  impose. That sweep is deliberately untrusting — if the address being retried
  were going to work, the retry loop would already have connected. With the
  field gone, this is also the only way a first-run install ever gets an
  address at all.
  The address readout only ever shows an address that means something at that
  moment: the one being probed while a sweep runs, the one that actually
  reached MiMo while connected, and nothing at all otherwise. In particular it
  stays blank while merely *connecting* — printing the address being retried
  next to "Conectando..." reads as though that address were live, which is
  exactly backwards when the usual reason for being stuck there is that the
  address is dead.
  While a sweep runs, the card's address readout shows the address currently
  being probed and the status line shows `Procurando MiMo... 87/253`, both
  repainted by a dedicated `SweepProgressTickInterval` (333ms) `DispatcherTimer`
  rather than by the progress reports themselves. `OnSweepProgress` only
  records where the sweep has got to; the tick is what paints. Repainting per
  report would be ~100 repaints a second — an unreadable blur — and a fixed
  tick also keeps the cadence independent of how the probes happen to bunch
  up (see `ProbeLaunchStagger`, which fixes the bunching itself).
  `ShowTransientStatus` exists because `ConnectionStatusText` is otherwise
  rewritten by the 200ms poll: a one-off message ("Endereço inválido", "MiMo
  não encontrado na rede") set directly would be overwritten on the very next
  tick, before anyone could read it — which was already true of the
  invalid-address message before the sweep existed.

- **Icons** come from the `MahApps.Metro.IconPacks.Material` NuGet package
  (Material Design Icons as `<iconPacks:PackIconMaterial Kind="...">`), not
  hand-drawn `Path` geometry — a first pass tried composing icons (a cloud,
  a brain) from overlapping `EllipseGeometry`/`RectangleGeometry` shapes
  stroked directly, which draws *every* edge where shapes cross, not just the
  outer silhouette, producing an illegible tangle; `CombinedGeometry` with
  `GeometryCombineMode="Union"` would fix that same trick, but a maintained
  icon set was simply more reliable than hand-authoring six icons. An
  implicit (no `x:Key`) `Style` targeting `PackIconMaterial` centers every
  icon in its badge — `Border` doesn't center a fixed-size child by default,
  so without it every icon sits pinned to the badge's top-left corner.
- **`src/mimo-trimmed.png`** is the supplied `mimo.png` wordmark with its
  transparent margin cropped off. The source is a 1254x1254 *square* for a
  wordmark that's actually wide and short (the glyphs occupy roughly
  x:206-1084, y:524-754 of that square) — displayed directly at a normal
  header height, almost all of that height is wasted transparent padding and
  the visible logo shrinks to an illegible sliver. Both files ship as
  `Resource` items in the csproj; only the trimmed one is referenced from XAML.
- **`RootScrollViewer.ScrollToTop()`** is called every time `ShowFromTray()`
  shows the window. WPF's default keyboard-focus-follows-into-view behavior
  auto-scrolls an ancestor `ScrollViewer` to whichever control ends up
  focused when the window becomes visible (e.g. the first checkbox) —
  without forcing it back to the top, the logo/subtitle at the very top of
  the card list would sit permanently scrolled just out of view.
- **Hora** and **Clima** are independent checkboxes (they used to be one
  combined "Previsão do tempo e hora" box) — Hora only drives the clock
  `DispatcherTimer`, Clima only drives `WeatherMonitor`; nothing else ties
  them together, so either can run without the other.
- **Pausa** is two `TextBox` fields ("Manhã"/"Tarde", free-typed `HH:mm`, no
  WPF `TimePicker` control in play — same "just type it" pattern Conexão's
  IP:porta field already uses) plus its own checkbox. `PausaCheckBox_CheckedChanged`
  starts a plain 1s `DispatcherTimer` (`_breakTimer`, same cadence as Hora's
  own clock timer — there's no "it's now HH:mm" event to react to, only wall-
  clock polling) that compares `DateTime.Now` against both parsed times each
  tick. `_breakMorningFiredOn`/`_breakAfternoonFiredOn` (`DateOnly?`) track
  the date each slot last fired on, so a slot fires exactly once per day
  instead of on every tick during that whole minute — and get reset to
  `null` whenever the checkbox is (re)checked, so toggling it off and back on
  the same day re-arms today's reminder instead of silently skipping it.
  On a match, `SendBreakReminder` sends one `NOTIFY COFFEE <msg>` line —
  Core's top-priority notification tier, which takes the whole screen for 10s
  and expires by itself (see PROTOCOL.md) — with the message randomly
  picked from `PausaMessages` (10 hardcoded PT-BR phrases, e.g. "Bora
  reabastecer o cafe!"). Unlike Clima's weather alert, no `FACE NEUTRAL`
  trick is needed here, and no `MSG` either: `NOTIFY` carries the
  expression and the text on one atomic line, which is the whole reason it
  exists (a two-command form could be caught half-applied, and this is the
  highest-priority thing the display shows). A notification expires on its
  own, so there's nothing to explicitly clear on uncheck, unlike
  MUSIC/WATCHING/PLAYING/THINKING elsewhere in this file.
- **Tema** is a `ComboBox` (`TemaComboBox`, `ThemeManager.Available`) picking
  between "MiMo Classic", "MiMo Matrix", "MiMo Mi2-Mo2" and "MiMo-84" — one control driving two
  unrelated systems: `ThemeManager.Apply` swaps this app's own WPF skin
  (`ThemeInfo.ResourcePath`), and `TemaComboBox_SelectionChanged` also sends
  Core its own `THEME <CoreTheme>` command (`ThemeInfo.CoreTheme`,
  `DEFAULT`/`MATRIX` — see PROTOCOL.md), which changes how Core itself draws
  the display. They just happen to both be about "appearance", and from the
  user's point of view MiMo Classic/MiMo Matrix reads as one choice, not
  two — this used to be two separate cards (Tema for the WPF skin, a "Tela
  do MiMo" checkbox for Core's `THEME`), folded into this one picker
  instead. "MiMo Matrix", "MiMo Mi2-Mo2" and "MiMo-84" all reuse the same
  `MiMoClassic.xaml` resource as "MiMo Classic" — there's no dedicated WPF
  skin for any of them in this app's own UI (yet), only for Core's display,
  so selecting them changes what Core shows without changing how
  Brobot.Sender itself looks. Like Clima's `WEATHER`
  badge, `THEME` is a persistent flag Core forgets across its own reboot, so
  `UpdateConnectionStatus` resends the selected `THEME` on reconnect
  (checking `TemaComboBox`'s currently-selected `ThemeInfo.CoreTheme`) the
  same way it resends the last weather reading — for any non-`DEFAULT`
  value, not just `MATRIX`: that check was literally `== "MATRIX"` until
  Mi2-Mo2 was added, which would have silently dropped the new theme on
  every reconnect. `DEFAULT` still needs no resend, since that's already
  Core's own boot default.
- **"Atividade da IA"** (a provider `ComboBox`: Claude/Codex/Gemini/Cursor/
  Outro, plus an **Instalar**/**Desinstalar** button — no checkbox) is the
  per-tool-hooks → local endpoint → `FACE`/`MSG` bridge. Unlike every other
  card, this one isn't a live toggle: installing edits the user's real,
  persistent Claude Code config (`ClaudeCodeHookInstaller`, below), so it's a
  deliberate, explicit, reversible action behind its own button rather than
  something that happens as a side effect of a checkbox. The button's own
  label always reflects the *actual on-disk* install state (re-read every
  time, not cached) and is only enabled when "Claude" is selected in the
  `ComboBox` — the other providers are UI/settings-persistence only so far,
  since only Claude Code hooks are actually wired up.
- **`ClaudeCodeHookInstaller.cs`**: edits
  `%USERPROFILE%\.claude\settings.json` — Claude Code's *global* settings, so
  the bridge applies to every project, not just whichever one happens to be
  open. Edits are surgical via `System.Text.Json.Nodes`, never a wholesale
  rewrite: every hook entry this class adds or removes is identified by its
  `"command"` containing `mimo-claude-hook.ps1` plus the specific event name,
  so any other hooks the user already has (their own, or another tool's) are
  left alone, and a matcher-group/event key is only pruned once it's been
  emptied entirely by removing *our* entries. `IsInstalled()`/`Install()` are
  idempotent — re-running `Install()` skips events that are already there
  instead of duplicating them, which also means a partial/broken install
  (e.g. the user hand-deleted one event) self-repairs on the next click
  rather than needing a manual uninstall first. The same click also wires up
  `settings.json`'s top-level `"statusLine"` (see `mimo-claude-statusline.ps1`
  below) — a single object, not an array of installable entries like
  `hooks`, so `Install()` only claims it when it's absent or already ours
  (`IsOurStatusLine`, matched the same way as hook entries — by the
  command containing `mimo-claude-statusline.ps1`), never overwriting a
  statusLine the user configured themselves. This is deliberately
  best-effort: `IsInstalled()` still only checks `hooks`, so a pre-existing
  foreign statusLine can't leave the "Instalar" button permanently unable
  to report success.
- **`hooks/mimo-claude-hook.ps1`** (repo root, copied to Brobot.Sender's
  output directory — see the csproj — so `ClaudeCodeHookInstaller` can point
  a hook command at a real file next to whichever `Brobot.Sender.exe` is
  actually running, not a hardcoded source-repo path): reads the hook's JSON
  off stdin, extracts a short label for `PreToolUse` (a
  Claude-tool-name → Portuguese-phrase table, e.g. `Bash` → "Executando
  comando...") and `Notification` (`.message`), and sends one line
  `EVENTNAME [text]` to `AiThoughtsListener`. Must never fail the hook or
  block Claude Code — every failure path (socket refused, bad JSON, anything)
  is swallowed and it always `exit 0`s — and must never write to stdout,
  since `UserPromptSubmit` hooks have their stdout appended straight into
  Claude's context.
- **`hooks/mimo-claude-statusline.ps1`** (same repo root / copy-to-output /
  path-resolution setup as the hook script above): registered as Claude
  Code's `statusLine` command, not a `hooks` entry — a genuinely different
  contract, not just another event. It receives a much richer JSON payload
  (`model`, `cost`, `context_window`, ...) than any hook gets, and — unlike
  `mimo-claude-hook.ps1`, where writing to stdout is actively forbidden —
  this script's own stdout *is* what Claude Code renders as the terminal's
  status line, so it always prints a one-line summary no matter what else
  happens, alongside forwarding the same text to `AiThoughtsListener` as a
  `ContextUsage` event. Computes "`N` tokens gastos na requisicao. `X`% do
  contexto utilizado" from `context_window.current_usage` (the last
  response's `input_tokens + output_tokens + cache_creation_input_tokens +
  cache_read_input_tokens`) and `context_window.total_input_tokens /
  context_window.context_window_size`. Two deliberate departures from what
  the field names suggest, both per Claude Code's own docs, not a bug here:
  `context_window.total_output_tokens` is *not* a running session total
  despite the name — it's just the most recent response's output tokens —
  so it's not used at all; and `total_input_tokens` (tokens currently
  loaded in the context window, which resets after a `/compact`) is what
  gets treated as "session total" for the percentage, per an explicit
  product call, since Claude Code exposes no field that's a true
  since-session-start running sum. `current_usage` is `null` before the
  first API call and again right after a `/compact`, so every field read
  is guarded and falls back to `0` — the terminal's status line must never
  go blank because of a null here, and neither must the MiMo side.
- **`AiThoughtsListener.cs`**: a plain `TcpListener` on `127.0.0.1:5591`
  (`MainWindow.AiThoughtsPort`), not `HttpListener` — `HttpListener` needs
  either Administrator or a `netsh` URL ACL reservation to bind a prefix on
  Windows, even a loopback one, which a tray app shouldn't require. Wire
  format is one line per event, `EVENTNAME optional free text...` (mirrors
  PROTOCOL.md's own `FACE`/`MSG` line shape, just inbound instead of
  outbound). Each connection is handled on a thread-pool thread (a hook
  invocation is a short-lived process: connect, write, exit), and
  `MainWindow.OnAiThoughtReceived` maps event names to Core commands:
  `UserPromptSubmit` → `FACE THINKING` + `MSG Pensando...`, `PreToolUse` →
  `FACE READING` (+ `MSG <text>` if the hook sent one), `Notification` →
  `MSG <text>` only (no `FACE` change — a notification isn't itself an
  expression), `Stop` → `FACE FINISHED` + `MSG Terminei!`, `SessionEnd` →
  `FACE NEUTRAL` + clear `MSG`, `ContextUsage` (from
  `mimo-claude-statusline.ps1`, not a hook — see below) → `MSG <text>` only,
  same no-`FACE`-change reasoning as `Notification`. THINKING is Core's one remaining sticky
  *foreground* expression (see PROTOCOL.md/Personality.cpp), so
  `_aiThoughtFaceActive` tracks it the same way `_mediaFaceActive`/
  `_gameFaceActive` track their own sticky *background* expressions —
  uninstalling (or the app exiting) while THINKING is showing explicitly
  sends `FACE NEUTRAL`/empty `MSG` (never `FACE IDLE` — that clears the
  other tier), or Brobot would be stuck glitching forever. `MainWindow`
  starts this listener
  whenever `ClaudeCodeHookInstaller.IsInstalled()` is true — on launch
  (`RestoreSettings`) and right after a successful Install — rather than
  gating it behind any checkbox state.
- **`SenderSettings.cs`**: a small JSON POCO persisted to
  `%AppData%\Brobot\mimo-sender-settings.json` — checkbox states, the chosen
  AI provider, and Core's TCP host/port (no Serial fields — see above).
  Checkboxes still take effect **immediately** when toggled regardless of saving (same
  `Checked`/`Unchecked` handlers as before); only *remembering that across a
  restart* requires clicking "Salvar configurações". On startup,
  `RestoreSettings()` reconnects to Core and re-checks whatever was saved,
  which replays the exact same handlers a manual click would — no separate
  "apply settings" code path to keep in sync with the live checkbox logic.
- **`WindowsMediaMonitor.cs`**: wraps `Windows.Media.Control.
  GlobalSystemMediaTransportControlsSessionManager` — the same "now playing" source
  the Windows volume flyout uses, so it picks up Spotify, a YouTube tab, VLC, etc.
  with no per-app login/API key. Needs the `net8.0-windows10.0.19041.0` TFM (not
  plain `net8.0-windows`) to unlock that WinRT projection. `FACE MUSIC` (source app
  ID contains "spotify") or `FACE WATCHING` (anything else — likely video) plus
  `MSG <artist> - <title>` on every real change; since MUSIC/WATCHING are Core's
  "sticky" background expressions (they hold until explicitly cleared, unlike
  everything else which auto-reverts after a few seconds), stopping playback or
  unchecking the box must explicitly send `FACE IDLE_MEDIA` + empty `MSG` or
  Brobot would be stuck dancing/watching forever. Not `NEUTRAL` — that only
  clears Core's foreground/AI-message tier (see PROTOCOL.md's FACE priority
  notes); sending it here would leave a stuck MUSIC/WATCHING alone while
  wrongly interrupting whatever the AI happened to be showing at that moment.
  And `IDLE_MEDIA` rather than bare `IDLE`, which clears the game tier too —
  stopping the music must not wipe a game that's still open.
- **`WeatherMonitor.cs`**: `Windows.Devices.Geolocation.Geolocator` for a one-time
  (per session) auto-located lat/long — weather doesn't need continuous GPS-grade
  tracking — then polls Open-Meteo (free, no API key/signup) every 30 minutes.
  Open-Meteo specifically because there's no public API for the weather data
  Windows' own taskbar widget shows (that's Microsoft's private MSN backend, not a
  system service like location or media sessions are) — this is the closest thing
  to a zero-friction alternative. Maps Open-Meteo's WMO weather codes down to the
  handful of pictograms Core actually has (`CoreConditionName`). The clock is a
  separate plain `DispatcherTimer` (Hora's own, independent of Clima) — `TIME` has
  no "changed" event to react to, it's just pushed on a fixed cadence, currently
  every second (a 1-minute cadence left the displayed clock up to 59s stale
  right after checking the box, since Core only ever shows what it was last told).
  Unchecking either sends empty `WEATHER` or `TIME` to clear that badge (see PROTOCOL.md).
  `MainWindow.OnWeatherUpdated` also tracks `_lastWeatherCondition` (separate
  from `_lastWeatherReading`, which exists purely for the reconnect-resend
  above) to catch an actual condition change between polls — sunny to rainy,
  say — and when one happens, sends `FACE NEUTRAL` + a random caring PT-BR
  heads-up from `WeatherAlerts` (`WeatherMonitor.cs`; 10 hardcoded messages
  per `WeatherCondition`, e.g. "Vai sair? Nao esquece o guarda-chuva!" for
  rain) *before* the routine `WEATHER` badge update. `FACE NEUTRAL` isn't
  decorative here — a bare `MSG` with no preceding `FACE` routes to
  whichever tier last sent one (`Personality::_lastCommandTier`, see
  PROTOCOL.md), which Clima never otherwise touches, so without it the alert
  could silently land in the background tier and never render if nothing's
  currently playing. `_lastWeatherCondition` starts `null` so the first
  reading after `Start()` never fires a false "changed" alert against
  nothing, and both it and `_lastWeatherReading` reset to `null` when Clima
  is unchecked so a later re-check starts a fresh baseline instead of
  comparing against a stale condition from a previous session. This is
  entirely a Sender-side feature — Core has no idea the weather changed,
  it's just another `FACE`/`MSG` pair as far as Personality.cpp is
  concerned; a per-condition Core expression/animation is a possible later
  addition, not built yet.
- **`SystemStatsMonitor.cs`** + **`AfterburnerSensors.cs`**: Game Mode's
  CPU/GPU/RAM figures, started only while `GameMonitor` reports a game and
  stopped the moment it doesn't — these exist for one screen, and polling
  hardware sensors for a display nobody is looking at is waste. Sends `STATS`
  (see PROTOCOL.md) every 2s.
  Two sources, because they have different guarantees.
  `LibreHardwareMonitorLib` supplies CPU load, RAM load, GPU load and GPU
  temperature, none of which need a kernel driver — the GPU pair comes from
  NVIDIA's own user-mode NVML — so those four work anywhere, with no install
  and no elevation. **CPU temperature is the odd one out and needs its own
  explanation**: it lives in an MSR, readable only from kernel mode, so every
  route to it runs through some driver. Measured on this machine: WMI's
  `MSAcpi_ThermalZoneTemperature` and its performance-counter twin both report
  a fixed 27.9 C that doesn't move under full load (an ACPI stub, not a real
  sensor), and LibreHardwareMonitorLib's own sensors read empty **even when
  elevated**, because the WinRing0 driver it used through 0.9.5 is on Windows'
  vulnerable-driver blocklist. 0.9.6 swapped that for the signed PawnIO, which
  does work — but PawnIO is a separate install, and it's blocked by FACEIT's
  anti-cheat, which matters rather a lot for a feature that only runs while
  you're gaming. So CPU temperature is instead borrowed from MSI Afterburner's
  `MAHMSharedMemory` block, which needs no admin and installs nothing: it
  reuses a driver Afterburner already installed for itself. That makes it
  **opportunistic, never required** — with Afterburner closed the field is
  null, Core draws `--`, and the other four are unaffected. Do not make it a
  hard dependency: it doesn't start with Windows here, and the installer ships
  to people who have never heard of it.
  Two details of that block are load-bearing and were found empirically:
  entries are matched on `szSrcName` (stable English, e.g. `"CPU temperature"`)
  rather than the localized name beside it, which is whatever language
  Afterburner's UI is set to; and Afterburner writes `FLT_MAX` into sensors it
  has no value for (Framerate reads that way with no game running), so
  anything not finite and plausible is discarded — unfiltered it reaches the
  screen as a 39-digit number. The header's own `dwHeaderSize`/`dwEntrySize`
  are used to walk the block rather than a compiled-in struct size, so a
  future layout change degrades instead of returning garbage. Afterburner also
  publishes **FPS**, which this doesn't use yet and which costs nothing to add.
- **`GameMonitor.cs`**: polls `Process.GetProcesses()` every 5s against Discord's
  public "detectable applications" catalog (`GET
  https://discord.com/api/v10/applications/detectable`, the same executable-name
  database Discord's own client uses for its "Playing X" status) — plain
  unauthenticated GET, same zero-friction reasoning as `WeatherMonitor` picking
  Open-Meteo. There's no OS-level "this process is a game" flag to query
  directly (Task Manager's Apps/Background/Windows-processes grouping is a UI
  heuristic, unrelated), and the registry store Xbox Game Bar uses
  (`HKCU\System\GameConfigStore`) only holds GameDVR/Xbox-Live title IDs, not
  executable paths — so riding Discord's community-fed catalog, the same
  technique Discord itself uses, is the closest thing to a generic answer. The
  ~10k-entry win32 executable map is filtered and cached to
  `%AppData%\Brobot\detectable-games-cache.json` (a week-long TTL) so the app
  works offline after the first fetch and doesn't redownload ~12MB every
  launch. Filtering excludes `is_launcher: true` entries (e.g.
  `LeagueClientUx.exe` — the menu/launcher process, not an actual match/session)
  and, more importantly, **generic runtime-host executables** the catalog
  sometimes lists as if they were a specific game's own binary — a real
  incident: the catalog's tModLoader entry lists `dotnet.exe` as one of its
  executables, which reported "playing tModLoader" on this dev machine the
  instant anything else was running via `dotnet` (i.e. constantly). Guarded
  two ways: a small hardcoded blocklist (`GenericRuntimeHosts` — dotnet, java,
  python, node, bash, busybox, common shells/browsers, etc.) plus a
  data-driven check that drops any executable basename claimed by **more than
  one** distinct game in the catalog (a generic/shared binary name is
  ambiguous regardless of whether it's on the hardcoded list). `CacheSchemaVersion`
  exists specifically so a cache file written before this filtering existed —
  otherwise indistinguishable from a fresh one, and easily surviving a full PC
  reboot since it's on disk — doesn't keep being trusted for its full 7-day TTL
  after the bug that produced it was fixed; bump it whenever the filtering
  logic changes. `Poll()` wraps each process's name lookup in its own
  try/catch — a handful of protected/system processes throw
  `Win32Exception` when queried without elevated rights, and an uncaught
  exception here previously killed the whole polling loop silently (fire-and-forget
  `Task`, no observer), leaving `GameChanged` stuck reporting the last game it
  saw forever, long after it had actually closed — this was a real bug, fixed
  once. `MainWindow.GameCheckBox_CheckedChanged` sends `FACE IDLE_GAME` +
  empty `MSG` *every* time monitoring starts (not just when turning it off) before
  starting `GameMonitor` — since `PLAYING` is sticky and this app has no way
  to ask Core what it's currently showing, a fresh launch's `_gameFaceActive`
  resetting to `false` would otherwise never notice (and never clear) a stuck
  sticky state left over from a previous crashed/killed session. `IDLE_GAME`, not
  `NEUTRAL` and not bare `IDLE`, same reasoning as `WindowsMediaMonitor` above.
- All three monitors, and `BrobotConnection` itself, raise their events off
  background threads — every handler in `MainWindow` re-enters via
  `Dispatcher.Invoke` before touching UI or calling `SendCommand`.
- The tray icon (`System.Windows.Forms.NotifyIcon` — WPF has no tray control of its
  own, hence `UseWindowsForms` alongside `UseWPF` in the csproj, which pulls
  `System.Windows.Forms`/`System.Drawing` into scope and makes `Application`,
  `MessageBox`, `Brushes`, `Color` etc. ambiguous with their WPF namesakes
  everywhere in this project — spelled out fully, or aliased as `Forms`/`Drawing`/
  `Media`, throughout) is built at runtime (`CreateTrayIcon`) from
  `src/mimo-b.png` (a "MiMo" wordmark on black, ships as a `Resource` item
  same as `mimo-trimmed.png`) via `Application.GetResourceStream` +
  `System.Drawing.Image.FromStream`, scaled down to the small size a tray
  icon actually needs — not a shipped `.ico` asset, and not the flat teal
  square it used to be before that PNG existed. `MainWindow.xaml`'s
  `Window.Icon` points at the same PNG, which covers both the title-bar icon
  and the taskbar icon (WPF uses one `Icon` property for both). `MainWindow` itself starts
  with no window shown at all — `App.xaml` has no `StartupUri`; `App.xaml.cs`
  constructs `MainWindow` directly without calling `Show()`, so there's no
  show-then-hide flash at launch. Closing the window (the X) hides it back to the
  tray instead of exiting — only the tray menu's "Sair" calls
  `Application.Current.Shutdown()`.

## Known hardware quirks

Opening the Uno's serial port (from PlatformIO upload, the WPF app, or any ad-hoc
script) resets the board via DTR. Commands sent immediately after `Open()` can be
lost while the bootloader is still active — wait ~2.5–4s after opening before
writing anything. This has been the cause of most "command didn't work" confusion
during development; when in doubt, check whether the WPF app already holds the
port before opening it elsewhere (`SerialPort` throws `UnauthorizedAccessException`
if so).

**ESP32-C3 SuperMini strapping pins**: GPIO2/8/9 are sampled at boot — an
external circuit holding one low can prevent boot or force download mode —
and GPIO8 additionally drives the board's onboard WS2812 LED. `Config.h`'s
`TFT_*_PIN` constants are chosen to avoid all three; don't reuse them for
anything else without re-checking.

**ESP32-C3 SuperMini's native USB-CDC + .NET's `SerialPort`**: this board's
USB port is the chip's own USB-Serial/JTAG peripheral, not a separate
UART-bridge chip like the Uno's — opening it can toggle DTR/RTS in ways that
made `System.IO.Ports.SerialPort.Open()` hang indefinitely against it during
development (see Brobot.Connection internals above for the fix). This is
specifically why `Brobot.Sender` connects over WiFi TCP instead of Serial
now; `Brobot.Display.Simulator` still uses Serial and has the same
background-thread fix, but hasn't been retested against this exact board.

## Installer (Brobot.Sender / "MiMo Sender")

`installer/BrobotSenderSetup.iss` + `installer/build-installer.ps1` package
Brobot.Sender for whoever assembled a Brobot — a real end-user installer, not
a dev tool, since this app is already the one branded "MiMo" throughout (see
Brobot.Sender internals above). Requires Inno Setup 6
(https://jrsoftware.org/isdl.php, not part of this repo/solution — installs
its own `ISCC.exe` compiler) on whichever machine builds the installer.

```powershell
C:\Projects\MiMo-Bot\installer\build-installer.ps1
# Output: installer\output\MiMoSenderSetup-<version>.exe
```

`build-installer.ps1` looks for `ISCC.exe` on PATH first, then falls back to
checking both Program Files locations *and*
`%LOCALAPPDATA%\Programs\Inno Setup 6` — `winget install
JRSoftware.InnoSetup` (no admin rights needed) installs there instead of
Program Files and doesn't add it to PATH, which is what this machine actually
has; missing that path is why the script initially failed to find it after a
winget install even though Inno Setup itself was fine.

The script `dotnet publish`-es Brobot.Sender as **self-contained win-x64**
(bundles the .NET 8 runtime) so the end user needs nothing pre-installed,
then hands the published folder to `ISCC.exe`. Installer is
**Brazilian-Portuguese-only** (`compiler:Languages\BrazilianPortuguese.isl`)
to match the app's own UI language throughout. The csproj's
`SatelliteResourceLanguages=pt-BR` trims WPF/WinForms' own per-language
satellite resource DLLs (ru, tr, zh-Hans, ...) out of that publish output —
dead weight for an app with no other UI language, and it visibly bloated the
self-contained build (publish folder dropped from ~190MB to ~174MB, the
compiled installer from ~57MB to ~53MB).

`PrivilegesRequired=lowest` + `DefaultDirName={autopf}\...` installs
per-user under `LocalAppData\Programs` with no UAC prompt by default (same
pattern VS Code/Discord use) rather than assuming the end user is a machine
admin — `PrivilegesRequiredOverridesAllowed` still lets a "Run as
administrator" launch install per-machine into Program Files instead.

`AppMutex=Brobot.Sender.SingleInstance` in the .iss matches a real named
`Mutex` `App.xaml.cs` now creates on startup (`_singleInstanceMutex`) — this
is what lets Setup detect a running MiMo Sender and offer to close it before
install/uninstall instead of failing on a locked .exe. The mutex is also
what makes a second launch a well-defined no-op (silently `Shutdown()`s
rather than opening a second tray icon/second `AiThoughtsListener` fighting
over port 5591) now that the installer can offer *two* separate shortcuts
that could both start it (Start Menu, and an optional "start with Windows"
shortcut in `{userstartup}`) — before the installer existed this was only
ever launched by hand, so the collision wasn't a real scenario yet.

The uninstaller deliberately leaves `%AppData%\Brobot` (settings, weather/
game caches) and any Claude Code hook entries `ClaudeCodeHookInstaller`
wrote to `%USERPROFILE%\.claude\settings.json` untouched — those are the
user's own data/config, not installed program files; anyone who installed
the hook should click "Desinstalar" on MiMo Sender's own Atividade da IA
card first, same as turning it off normally.

`src/Brobot.Sender/src/mimo.ico` is a multi-resolution icon generated from
the same `mimo-b.png` the runtime tray icon already uses (see
`CreateTrayIcon`), so the taskbar/shortcut/installer icon all match — it
isn't hand-drawn, and there's no build step that regenerates it
automatically, so re-run the generation if `mimo-b.png` ever changes. Wired
in via `<ApplicationIcon>` in the csproj, which bakes it into the .exe
itself; this is separate from and in addition to `MainWindow.xaml`'s own
`Window.Icon` (title bar only, PNG, resolved at WPF startup rather than at
the PE level).

## Testing tips

- The most reliable way to verify Arduino-side behavior is reading the raw serial
  protocol directly (bypassing the WPF app), since the wire format is simple text.
- The WPF app's connect/disconnect and command-send flow can be driven headlessly
  via Windows UI Automation for scripted screenshots — see conversation history/PR
  descriptions for examples if needed again.
