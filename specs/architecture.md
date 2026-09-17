# Architecture

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
