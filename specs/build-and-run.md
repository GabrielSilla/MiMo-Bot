# Build & run

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
(see [architecture.md](architecture.md) for the display specifics):

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

WiFi has no hardcoded credentials — on first boot (or once none of the last
5 networks MiMo has connected to can be reached), the board serves its own
**"MiMo-Setup"** access point with a small captive-ish config page at
`http://192.168.4.1` (scans and lists nearby networks instead of requiring
the SSID to be typed by hand; `WifiSetup.cpp`, see
[firmware-platform.md](firmware-platform.md) for the multi-network
remember/promote logic). Submitting the form saves the credentials to flash
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
# Optional arg: brobot_native.exe [port] — the port only, defaulting to 5555.
# The host is always 127.0.0.1 (main_native.cpp reads argv[1] as the port),
# so passing an address there silently listens on a nonsense port instead.

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
