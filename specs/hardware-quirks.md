# Known hardware quirks

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
development (see [connection.md](connection.md) for the fix). This is
specifically why `Brobot.Sender` connects over WiFi TCP instead of Serial
now; `Brobot.Display.Simulator` still uses Serial and has the same
background-thread fix, but hasn't been retested against this exact board.
