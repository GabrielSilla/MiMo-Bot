# BrobotCore native (dev-only, no Arduino)

Runs the same personality/protocol/face logic as the real firmware
(`Face.cpp`, `Personality.cpp`, `Protocol.cpp` — unmodified, shared with the
AVR/ESP32 builds) as a plain Windows console executable, so you can iterate
without an Arduino attached.

**Not part of the PlatformIO project** — `platformio.ini` and the
`uno`/`esp32dev` envs are untouched. This is a separate build, compiled
directly with MSVC (`cl.exe`), because this machine has no gcc/MinGW for
PlatformIO's `native` platform to use.

## How it connects

The real firmware talks to PC apps over a serial `Stream`. On the host
there's no UART, so `native/include/TcpBroadcastStream.h` implements that
same `Stream` interface over TCP instead — same
[PROTOCOL.md](../../PROTOCOL.md) line protocol either way, just a different
wire.

**Core listens; PC apps connect to it.** This mirrors a real COM port: the
device doesn't dial out to you, whatever PC app wants to talk to it opens
the connection. Unlike a real COM port (exclusive to one process), Core's
TCP listener accepts **multiple simultaneous clients** — purely a dev
convenience real serial can't offer, added specifically so Brobot Virtual
Display (watching the face) and Brobot.Sender (sending FACE/MSG/WEATHER/TIME)
can both be connected at once, each doing its own thing:

- Every draw-protocol line is **broadcast** to all connected clients.
- Control-command bytes (FACE/MSG/WEATHER/TIME) are accepted from **any**
  connected client and merged into the one `Protocol`/`Personality` Core runs.
- A stalled/non-draining client (a 200ms send timeout) gets dropped rather
  than blocking draw delivery to everyone else.

Because Core is the listener, it can be started in either order relative to
the PC apps and restarted as often as you like — each app's `BrobotConnection`
retries its connection on its own (see `Brobot.Connection/BrobotConnection.cs`)
without needing to be relaunched.

## Build

```powershell
BrobotCore\native\build.ps1
```

Finds Visual Studio via `vswhere` and loads its MSVC environment
automatically (skips that step if `cl.exe` is already on PATH, e.g. run
from a "Developer PowerShell for VS"). Requires the "Desktop development
with C++" workload. Output: `native\build\brobot_native.exe`.

Use `-Clean` to wipe `native\build` first.

## Run

1. Run `native\build\brobot_native.exe [port]` (default port `5555`). It
   listens immediately and prints connect/disconnect activity — no client
   needs to be present yet.
2. Build and run Brobot Virtual Display and/or Brobot Sender
   (`dotnet build BrobotVirtualDisplay.slnx`). Virtual Display has the
   `127.0.0.1`/`5555` fields right on its main window — click **"Conectar
   (dev, sem Arduino)"**. Sender starts in the system tray with no window;
   click its tray icon, then **"Configurar conexão..."** to reach the same
   fields. Virtual Display shows the eyes reacting; Sender's checkboxes
   (weather+clock, Windows media, ...) push `FACE`/`MSG`/`WEATHER`/`TIME` to
   Core in the background once connected.

## What's shimmed

`native/include/Arduino.h` is a minimal stand-in for `<Arduino.h>` — just
enough for the shared logic to compile unmodified: the integer typedefs,
`Stream`/`print`/`println`, `F()`/`__FlashStringHelper`, and `random()`.
It's only ever added to the include path for this native build; the real
AVR/ESP32 builds never see it.
