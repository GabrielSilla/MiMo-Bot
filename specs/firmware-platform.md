# Firmware Internals — WifiSetup, Buzzer, Protocol.cpp, Minigames, native build

- **`WifiSetup.cpp`** (ESP32 only, see `WifiSetup.h`): `connectOrStartPortal()`
  remembers up to `kMaxSavedNetworks` (5) networks in `Preferences`/NVS —
  SSID+password pairs under `ssid0`/`pass0` through `ssid4`/`pass4` plus a
  `count` key — most-recent-first, rather than just one, since Peemo moves between a
  small set of known places (home, a friend's place, ...) rather than
  staying on a single network. On boot it tries each saved network in that
  order, one `kConnectTimeoutMs` (5s) attempt each; a hit anywhere but the
  front (`tryConnectSavedNetworks`) promotes that network back to
  most-recent via `rememberNetwork` (move-to-front, evicting the oldest past
  5) and persists the reordered list — so a place Peemo visits often floats
  to the top and survives longest once the list fills up. Only once every
  saved network fails does it fall back to the "Peemo-Setup" config-portal AP
  — see [build-and-run.md](build-and-run.md) for the user-facing flow.
  Submitting the portal's form calls the same `rememberNetwork` to add (or
  promote) that network, so a network entered by hand gets the identical
  most-recent-first treatment as one that was actually confirmed to
  connect — there's no separate "add" path. This is a from-scratch key
  scheme, not a migration of the old single-`ssid`/`pass`-key format: a
  device already running the earlier single-network firmware finds nothing
  under the new keys on its first boot after the upgrade and goes straight
  to the portal once, same as a brand-new board.
  `connectOrStartPortal()` takes an optional per-tick callback so
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
  exactly one reason: `PING` answers `PEEMO 1` back down it. That's the only
  command Core replies to and the only one routed to neither `Personality` nor
  `DeviceSettings` — it says nothing about Brobot, only that this is a Brobot,
  which is precisely what `PeemoDiscovery`'s network sweep needs to hear (see
  [connection.md](connection.md)). The reply's trailing number is a protocol
  revision, there so a future PC app can tell an old board from a new one
  without a second round trip; bump it only for changes a client would
  actually have to branch on. `dispatch` is also the one place that
  arbitrates between the two exclusive minigames: the `PONG`/`RPG` branches
  each check the *other* one's `isActive()` before forwarding a command, so
  `PongGame`/`RpgBattle` stay as unaware of each other as `Personality` and
  either of them already are — a `PONG START` arriving mid-battle (or vice
  versa) is simply dropped rather than one minigame clobbering the other's
  exclusive screen.
- **`PongGame.cpp`/`RpgBattle.cpp`** (Peemo's two minigames, launched from
  Brobot.Sender's Anti-Stress card): both follow the same shape —
  `onCommand`/`update`/`render`/`isActive`/`justEnded` — and the same
  bypass `main.cpp` already used for the WiFi setup portal: while
  `isActive()`, `loop()` calls their `update`/`render` *instead of*
  `personality.update()`/`renderPersonalityFrame()`, not as a new
  `Personality::Tier` — so a round/battle in progress is immune to
  everything, `NOTIFY` included, until it ends. `justEnded()` is
  edge-triggered (true exactly once, cleared on read) and is what tells
  `main.cpp` to write one `PONG OVER <score>`/`RPG OVER <result>` line
  straight to the active `Stream` — the same "reply directly, don't route
  through Personality" idiom the `PING` reply above already uses — followed
  by a `PRESENT` so it flushes through `BrobotConnection`'s existing
  frame-batching on the PC side with no protocol-level plumbing added.
  Neither class calls `justEnded()`-triggering logic from its own `stop()`:
  a `PONG`/`RPG STOP` (Escape, from Brobot.Sender) means the PC side already
  knows the round/battle is over because it just sent that command, so no
  notification line is needed there — only a Core-decided ending (missed
  the ball; victory/defeat/Fugir) fires one. Both draw with plain `fillRect`/
  `drawRect`/`drawText` only — `IDisplay::drawRoundedRect` turned out to be
  an *outline*, not a fill, on both real implementations (Adafruit_GFX's
  `drawRoundRect` on the physical build, an outer-minus-inner ring on the
  Simulator), so neither minigame's sprites attempt a filled disc the way an
  early draft assumed Face.cpp's own icons did.
  `PongGame` is a small continuous-physics sim (paddle/ball as floats,
  updated every `update(now)` call via a stored `dt`, same as any other
  per-frame integration) with a `PLAYING`/`GAME_OVER` sub-state.
  `RpgBattle` is a state machine instead (`PLAYER_MENU` → `SPELL_MENU`/
  `TARGET_SELECT` → `PLAYER_ACTION` → `ENEMY_ACTION` → back to
  `PLAYER_MENU`, or `VICTORY`/`DEFEAT`/`FLED`), closer in spirit to
  `Face.cpp`'s anchored-timestamp animations than to `PongGame`'s
  physics — `render(display, now)` takes `now` and recomputes each
  animation's `elapsed` from a stored start timestamp, rather than
  `PongGame`'s style of precomputing everything in `update()`. Damage/heal
  is rolled and applied at the animation's *impact keyframe* (roughly its
  halfway point), not the instant a menu choice is confirmed, so an HP bar
  only moves when the hit visibly lands. Player damage (Atacar/Bola de
  Fogo, `RPG_PLAYER_ATTACK_DAMAGE_MIN/MAX` in `Config.h`) rolls higher than
  enemy damage and Cura's heal (`RPG_DAMAGE_MIN/MAX`) — a real balance
  fix: with 2 enemies at 50 HP each hitting back for the same 1-10 every
  round, a symmetric roll made a "quick anti-stress" battle drag on far
  longer than intended.
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
