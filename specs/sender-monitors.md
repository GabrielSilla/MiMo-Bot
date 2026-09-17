# Brobot.Sender Internals — Live Data Monitors (Media, Notifications, Weather, Stats, Games)

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
- **`NotificationMonitor.cs`**: shows Windows' own toast notifications (any
  app, not just this one) on MiMo, via `Windows.UI.Notifications.Management.
  UserNotificationListener` — the same WinRT surface Action Center itself is
  built on. That API's own docs (and this project's own first assumption)
  gate it behind package identity (MSIX/UWP) — a real sparse-package
  proof-of-concept (self-signed cert, signed `.msix`, `PackageManager.
  AddPackageByUriAsync` with an `ExternalLocationUri`) was built and then
  thrown away once testing showed it wasn't needed: `RequestAccessAsync`
  and `GetNotificationsAsync` both work fine from this app's plain Win32
  process with zero extra packaging, the same as `WindowsMediaMonitor`/
  `WeatherMonitor`'s WinRT calls already do. The one member that *does*
  still need identity is the live push event, `NotificationChanged` —
  subscribing to it throws `COMException 0x80070490` ("element not found")
  even right after `RequestAccessAsync` returns `Allowed` — confirmed by
  hitting it for real, not assumed. So this polls `GetNotificationsAsync`
  on a timer (`PollInterval`, 3s) instead, the same "no changed event
  available, so ask on a schedule" shape `WeatherMonitor`'s clock and
  `GameMonitor`'s process list already use, deduping against a `HashSet<uint>`
  of notification ids rebuilt from the current listing each poll (so a
  dismissed id doesn't stay "seen" forever). `StartAsync` seeds that set from
  whatever's already sitting in Action Center *before* starting the poll
  loop, specifically so checking the box doesn't dump a burst of stale
  notifications onto the screen. Each new one sends one atomic
  `NOTIFY READING <app>: <text>` — Core's top-priority tier, not a plain
  `FACE`/`MSG` pair: a Windows notification is already an interruption on
  the PC side, so anything less on MiMo would undersell it. `READING` for
  the same "look over here" reasoning `Atividade da IA`'s own
  `PermissionRequest` → `NOTIFY` already uses.
  `MainWindow.NotificationsCheckBox_CheckedChanged` has one thing worth
  calling out: setting `NotificationsCheckBox.IsChecked = false` from
  inside the failure branches would normally re-enter the very same handler
  through its own `Unchecked` wiring (same Checked/Unchecked-to-one-method
  pattern every checkbox card here uses) — and that re-entrant call takes
  the plain `else` branch, which unconditionally clears the status text,
  wiping out the failure message that was just set a moment earlier before
  anyone could read it. `UncheckNotificationsWithoutClearingStatus`
  detaches the handler for that one assignment and reattaches right after,
  which is what actually surfaced the `COMException` above instead of a
  status line that "flashed and vanished" — this exact class of bug likely
  also affects `MediaCheckBox`'s identical catch-block pattern, not fixed
  here since it wasn't the thing asked for.
- **`TeamsNotificationWatcher.cs`**: a second, independent notification
  capture path, alongside `NotificationMonitor` above and started/stopped
  by the same Notificações checkbox — for exactly one app, Microsoft Teams,
  confirmed (not assumed) to need it: Teams never registers a real Windows
  toast at all, so `UserNotificationListener` — the whole platform
  `NotificationMonitor` watches — has zero visibility into it no matter how
  it's used. Confirmed the hard way across several dead ends before finding
  this one: no entry for Teams in the registry key that lists every app
  that's ever shown a toast; `EnumWindows` polling found no new top-level
  window when a Teams notification fired; `UIA` structure-changed events
  never fired even once, not even for ordinary chat activity; and Teams'
  main-window UIA tree was completely flat — a stack of nameless, childless
  `Pane`s — even with Windows Narrator running (the strongest available
  trigger for Chromium's full accessibility tree). What finally worked:
  Teams' notification banner turns out to be a small *second* top-level
  window (same process, same `TeamsWebView` window class as the huge main
  window, but only ~370px wide), created once and Shown/Hidden in place per
  notification rather than recreated — invisible to plain `EnumWindows`
  polling because of exactly that reuse, but not to a proper
  `SetWinEventHook(EVENT_OBJECT_SHOW, ...)`, the same event-driven primitive
  every real accessibility tool is built on. *That* window's own UIA tree
  turned out to be fully populated (unlike the main window's), several
  levels deeper than a first attempt's depth limit suggested — the real
  text only showed up once the walk went ~20 levels down into Teams' own
  nested Fluent UI markup.
  Hooks globally (`idProcess=0`, not scoped to Teams' PID at install time)
  because Teams can restart with a new PID at any point while the checkbox
  stays checked, so `OnWinEvent` re-checks the owning process by name
  (`ms-teams`) on every single system-wide `EVENT_OBJECT_SHOW` instead of
  filtering by a PID captured once — the same "don't assume, re-verify"
  reasoning `GameMonitor` already applies per-poll for its own process list.
  Window width alone (`MaxPopupWidthPx`, 800) is what tells the popup apart
  from the main window, since both share process, window class, and title
  ("Microsoft Teams") — the main window is always far wider than any
  monitor's small corner popup, so this one crude number does the job
  without needing anything more specific to key on.
  `ExtractNotificationText` finds the popup's `ControlType.Document` (its
  WebView2 content root, `RootWebArea`) and reads every `ControlType.Text`
  descendant's accessible `Name` beneath it — filtering out the app name
  itself and a redundant screen-reader-only composite label Teams renders
  as a sibling of the real body text (`MessagePreviewPrefix`,
  `"Visualização da mensagem"`) so the extracted string doesn't repeat
  itself. That prefix is the literal PT-BR string this project's own dev
  machine's Teams renders — a different display language changes it
  outright, and there's no reasonable way to generalize this without real
  evidence of what those other strings actually are, so it isn't attempted.
  This whole class is explicitly the more fragile half of the Notificações
  feature (see its own header comment) — a Teams update, UI redesign, or
  display-language change can silently stop it working with no error
  surfaced anywhere, unlike `NotificationMonitor`'s WinRT path, which is a
  stable public API. Accepted deliberately, after confirming there was no
  path with Teams' cooperation at all.
  `_lastText`/`_lastRaisedAtUtc`/`DedupeWindow` (3s) exist because the same
  banner re-fires `EVENT_OBJECT_SHOW` several times while it animates in and
  out — without this, one real Teams notification would turn into a burst
  of duplicate `NOTIFY`s on MiMo. The `WinEventDelegate` callback is kept as
  an instance field, not a local — `SetWinEventHook` does not root
  the delegate itself, so a GC'd delegate crashes the process the next time
  Windows tries to invoke a hook nobody kept alive. Must be `Start()`-ed
  from a thread with a live Windows message pump (`WINEVENT_OUTOFCONTEXT`
  delivers callbacks through the calling thread's own queue) — the WPF UI
  thread already provides one, so `MainWindow` calls `Start()` directly
  rather than from a background `Task`, which would install the hook but
  never actually receive a single callback.
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
  publishes **FPS** (`AfterburnerSensors.Framerate`), which `SystemStatsMonitor.Read()`
  now reads unconditionally rather than only when CPU/GPU load or temperature
  is still missing — it's the one field with no other source at all, so
  there's no "something else already found it" case to gate on the way the
  other four are. It rides the same `STATS` line as everything else (a new
  6th field, `PROTOCOL.md`), and on Core it's drawn with no `%`/`C` suffix
  (`formatStatValue`'s suffix `'\0'` case) since it's a plain count, not a
  load percentage or a temperature — and with no bar in MI84's MONITOR tab,
  since a load/temperature bar assumes a 0-100 range and a high-refresh
  monitor routinely clears 100 FPS. `CLASSIC`'s own Game Mode panel
  (`drawStatsMessage`) does show it, but not as a fourth stacked row — its
  box height is the same fixed constant as before (`STATS_BOX_LINES_CLASSIC`),
  so a fourth row would eat into the game name's own space instead. Passing
  `gridLayout = true` packs all four readings into a 2x2 grid instead
  (`STATS_GRID_ROWS` = 2, `STATS_GRID_COL2_X` splitting the box's usable
  width into two columns): `FPS`/`RAM` share the top row, `CPU`/`GPU` the
  bottom — the same pairing logic as before (neither FPS nor RAM has a
  temperature to show, same as neither of CPU/GPU's own column-mates would
  need one), which is what makes two readings fit on one row at all. This
  actually frees a row versus the old one-per-row layout (2 rows instead of
  3), so the game name gains a line rather than losing one. `MI2MO2` calls
  the same function with `gridLayout = false` and is completely unchanged —
  still the original one-per-row CPU/GPU/RAM list, still no FPS — because
  its box height (`STATS_BOX_LINES_MI2MO2`) is already at the most it can
  grow without covering the bottom of R2's lens, so there's no slack to
  spend on a fourth reading regardless of layout. `MATRIX`/`MI84`'s MONITOR
  tabs (log-based, not box-based) show FPS as their own fourth row, per
  above.
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
  `%AppData%\Brobot\detectable-games-cache.json`, but the cache is no longer
  trusted by age — `GameMonitor.Start()` re-fetches and re-filters it every
  time (i.e. every Sender launch with Jogos checked, since `RestoreSettings`
  replays the checkbox the same way a manual click would), not just once a
  fixed TTL had elapsed. A week-long TTL used to gate this, and meant a game
  Discord added to the catalog could sit unrecognized for up to a week — the
  literal "jogo novo não aparece" complaint this refetch-on-start replaced it
  to fix. The on-disk cache still matters: `LoadCache()` seeds `Poll()` with
  something to match against immediately (rather than waiting on the
  network), and is what a failed/offline refresh falls back to. Filtering
  excludes `is_launcher: true` entries (e.g.
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
  reboot since it's on disk — doesn't keep being trusted for the brief window
  before the next refresh lands, or as the fallback if that refresh fails,
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
