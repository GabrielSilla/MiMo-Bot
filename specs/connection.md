# Brobot.Connection internals

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
  (WiFi-only now — see [sender-overview.md](sender-overview.md)'s Conexão card); `Brobot.Display.Simulator`
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
