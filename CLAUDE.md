# Brobot

A desktop buddy robot. Brobot Core (Arduino/native C++) owns all personality
and decides what to show; PC-side apps (Brobot Virtual Display, Brobot
Sender) only render or send commands over a text-line protocol. Full detail
lives in `specs/` — this file is just the index. **Read the relevant spec
file(s) before working on a given part of the system**; don't rely on
filenames alone to guess behavior, this codebase has a lot of "why" that
isn't visible from the code.

`PROTOCOL.md` (repo root) remains the single source of truth for the wire
protocol itself — `specs/protocol.md` is a summary/index into it, not a
replacement.

## Start here

- [specs/overview.md](specs/overview.md) — project diagram, **the one rule
  that matters** (PC apps render/send, Core decides — read this first),
  and the full repository layout with pointers into every other spec file.
- [specs/architecture.md](specs/architecture.md) — `IDisplay`, the three
  display implementations, `vscreen`, `Personality`/`Face`, `BrobotConnection`
  at a glance.
- [specs/protocol.md](specs/protocol.md) — wire command summary and the
  FACE/MSG priority-tier rules (NOTIFICATION > FOREGROUND > GAME > MEDIA).
- [specs/build-and-run.md](specs/build-and-run.md) — build/flash commands for
  the WPF apps, the Uno, the ESP32-C3 (incl. pin wiring, WiFi setup flow),
  and the native (no-Arduino) dev build of BrobotCore.

## Firmware (BrobotCore)

- [specs/firmware-face-core.md](specs/firmware-face-core.md) — `Face.cpp`'s
  base eye/expression rendering (HAPPY/SAD/FAILED/READING/THINKING/PLAYING/
  COFFEE/weather+clock badges/...).
- [specs/firmware-face-themes.md](specs/firmware-face-themes.md) —
  `Face.cpp`'s whole-frame theme reskins: `Theme::MATRIX`, `Theme::P2M2`
  (R2D2), `Theme::PEEMO84` (amber CRT terminal).
- [specs/firmware-personality.md](specs/firmware-personality.md) —
  `Personality.cpp`: blink/look-around, priority tiers, boot animation,
  `SLEEPY`/bedtime messages.
- [specs/firmware-platform.md](specs/firmware-platform.md) — `WifiSetup.cpp`
  (multi-network remember/reconnect + config portal), `Buzzer.cpp`,
  `Protocol.cpp`, the `PongGame`/`RpgBattle` minigames, the native build's
  `TcpBroadcastStream`, and the `FRAME_INTERVAL_MS` baud-budget caveat.

## PC side

- [specs/simulator.md](specs/simulator.md) — Brobot.Display.Simulator (WPF):
  `Font5x7`, `SerialDisplayBridge`, `MainWindow`.
- [specs/connection.md](specs/connection.md) — `Brobot.Connection`:
  `BrobotConnection` (Serial/TCP, frame batching) and `PeemoDiscovery`
  (network sweep to find Peemo's DHCP-assigned IP).
- [specs/sender-overview.md](specs/sender-overview.md) — Brobot.Sender
  (Peemo Sender) branding/tabs overview, the Conexão card, Tema/ClassicColor,
  and misc UI (icons, wordmark, tray icon).
- [specs/sender-minigames-achievements.md](specs/sender-minigames-achievements.md)
  — the Mini Games tab (Pong/Batalha RPG launch flow, `GlobalKeyboardHook`)
  and the Conquistas tab (`AchievementMonitor`/`AchievementCatalog`).
- [specs/sender-feature-cards.md](specs/sender-feature-cards.md) — the
  simpler checkbox cards: Hora/Clima, Pausa (break reminders), Alertas de
  desempenho (CPU/RAM > 90%), Notificações (intro), Build (Gradle/MSBuild/Visual Studio detection).
- [specs/sender-monitors.md](specs/sender-monitors.md) — the live data
  sources: `WindowsMediaMonitor`, `NotificationMonitor` +
  `TeamsNotificationWatcher`, `WeatherMonitor`, `SystemStatsMonitor` +
  `AfterburnerSensors`, `GameMonitor`.
- [specs/sender-ai-bridge.md](specs/sender-ai-bridge.md) — Atividade da IA:
  `ClaudeCodeHookInstaller`, `hooks/peemo-claude-hook.ps1`,
  `hooks/peemo-claude-statusline.ps1`, `AiThoughtsListener`, `SenderSettings`.

## Ops

- [specs/hardware-quirks.md](specs/hardware-quirks.md) — Uno DTR reset
  timing, ESP32-C3 SuperMini strapping pins, native USB-CDC + `SerialPort`
  quirks.
- [specs/installer.md](specs/installer.md) — `build-installer.ps1` /
  Inno Setup packaging of Peemo Sender, incl. the silent `Brobot.VSExtension`
  VSIX install.
- [specs/testing.md](specs/testing.md) — verifying firmware/WPF behavior.
