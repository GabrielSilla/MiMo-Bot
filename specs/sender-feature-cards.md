# Brobot.Sender Internals — Simple Feature Cards (Hora, Clima, Pausa, Relatório, Notificações, Ferramentas de Dev)

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
- **Relatório** is one `TextBox` ("Horário", free-typed `HH:mm`, defaulting
  to `"18:00"`) plus its own checkbox — the exact same
  `DispatcherTimer`/once-per-day-guard shape as Pausa's two slots, just one
  slot (`RelatorioCheckBox_CheckedChanged`/`CheckReportTime`, mirroring
  `PausaCheckBox_CheckedChanged`/`CheckBreakTime`). On a match,
  `SendDailyReport` (via the shared `SendReport(heading)`) sends one
  `REPORT <buildOk> <buildFail> <meetingMin> <mediaMin> <gameMin> <RATING>
  <texto>` line (see PROTOCOL.md) — same atomic/top-priority tier as
  `NOTIFY`, but its own top-level command, same reasoning `ACHIEVEMENT`
  already established: it carries more structure (five numbers plus a
  rating token) than a plain expression+text notification can. Sender only
  ever hands over `DailyReportTracker.BuildReport()`'s raw numbers and
  `DailyReportMessages.WireToken(rating)` — Core decides how those numbers
  actually read on screen (six stacked stat lines with small top-pinned
  eyes, see `specs/firmware-face-core.md`'s `drawReportNotification`), not
  Sender; an earlier version hand-formatted one long `NOTIFY <FACE> <text>`
  string here and it read badly on the real display, everything running
  together instead of being scannable per item — REPORT replaced it.
  Unlike every other card here,
  Relatório doesn't watch a live signal of its own; it sums signals the
  other cards already raise (`DailyReportTracker`, mirroring
  `AchievementMonitor`'s own accumulate-daily-counters/`Tick`/
  `RollOverDayIfNeeded` shape almost exactly, but as its own class over its
  own `daily-report.json` — unrelated concern from achievements, kept
  separate the same way `SenderSettings` and `AchievementProgress` already
  are): `RecordBuildSuccess`/`RecordBuildFailure` from
  `OnBuildStateChanged`'s `Successful`/`Failed` cases (not `Started`, or one
  build would double-count), `SetMeetingActive` from `OnLiveCallChanged`
  (`_activeCalls.Count > 0`), `SetGameActive` from `OnGameChanged`/Jogos'
  unchecked branch, and `SetMediaActive` from `OnNowPlayingChanged`/Mídia's
  unchecked branch — deliberately broader than `AchievementMonitor.
  SetMusicActive`, which is audio-only for the unrelated Audiophile
  criterion: the report's "tempo de mídia" counts music *and* video, since
  it's only ever logged, never scored (see below). None of this is gated on
  Core being connected — meeting/media/game detection all happen at the OS
  level — so `Tick()` runs off the same 200ms `_connectionStatusTimer` poll
  `AchievementMonitor.Tick` already rides, just without that one's
  `connected` gate.

  `DailyReportScoring.Evaluate` turns the day's raw numbers into a
  `DailyPerformanceRating` (`Pessimo`/`Ruim`/`Questionavel`/`Medio`/`Bom`/
  `Excelente`) via one weighted formula, kept in its own file specifically
  so the constants are easy to retune after seeing real days play out:
  a `Baseline` of 50 (a totally quiet day lands exactly on Médio — "nothing
  happened" isn't a verdict in either direction), successful builds worth
  more than failed ones but **both positive** (either one means work
  happened — a deliberate product decision, not an oversight), meeting time
  worth points per 30 min (it's work too), and game time free for the first
  30 min/day then penalized on a **triangular ramp** per extra 30-min block
  (block *n* costs `n * GamePenaltyPerBlock` on top of every block before
  it, so a two-hour session hurts far more than proportionally) —
  `GamePenalty`'s own comment has the exact block math. Media minutes are
  computed and returned alongside the score but never feed into it: logged,
  never judged, per the same product decision. `DailyReportMessages` picks
  the casual PT-BR line that goes with whichever rating came out (one flat
  pool per rating, same `GreetingMessages`/`PausaMessages` shape and voice
  rules — casual buddy tone, never implies memory of *other* days, only
  today's own numbers) plus, via `WireToken`, the `<RATING>` token REPORT's
  wire line carries — `RatingLabel` is a third, separate mapping, used only
  for Sender's own `RelatorioStatusText` UI, unrelated to what Core draws.

  CTRL+SHIFT+F10 is a second, independent trigger for the same report,
  system-wide regardless of window focus (or whether Brobot.Sender's window
  is even shown) — registered once in `MainWindow`'s constructor via
  `GlobalHotkey` (`RegisterHotKey`/`WM_HOTKEY`, a different mechanism from
  `GlobalKeyboardHook`'s continuous low-level hook — see its own header
  comment for why). Plain `ALT+F10` was the first choice and was dropped:
  confirmed live (`RegisterHotKey` returning false) that something else on
  a real dev machine already owns it — likely OEM/laptop software grabbing
  a bare F-key combo, common enough that a two-modifier combo is the safer
  default. `ShowPartialReport` calls the exact same
  `SendReport(heading)` the scheduled 18h path uses (`SendDailyReport`),
  just with a different heading — `BuildReport()` has no notion of "final",
  it's always just "what's accumulated so far", so there's nothing else to
  distinguish. Deliberately independent of the Relatório checkbox/timer
  state and of `_reportFiredOn`: checking in early must not skip (or
  double-fire) the scheduled report later, and this works even with the
  card's own checkbox unchecked, since `DailyReportTracker` accumulates
  unconditionally (see above) — the checkbox only gates the 18h auto-fire.

- **Notificações** is a plain checkbox card, same shape as Mídia/Jogos, that
  starts two independent watchers at once — see `NotificationMonitor.cs`
  below for the main one (polling Windows' own notification platform) and
  `TeamsNotificationWatcher.cs` for the second, Teams-only one it needed on
  top of that, and why.
- **Ferramentas de Dev** (still `BuildCheckBox`/`SenderSettings.BuildEnabled`
  internally — only the card's user-facing text changed when git joined it,
  to avoid losing existing users' saved checkbox state on upgrade) is a
  plain checkbox card, same shape as Mídia/Jogos, whose checked state
  starts/stops four things: two of this app's own build monitors —
  `GradleBuildLogMonitor` (tails a Gradle daemon log for `BUILD SUCCESSFUL`/
  `BUILD FAILED`) and `MsBuildProcessMonitor` (polls WMI for
  `MSBuild.exe`/`dotnet.exe` processes, excluding any `/nodemode`-tagged
  worker to avoid counting one real build several times over — confirmed
  live that a plain `dotnet build` spawns several of those) — plus
  `GitHookInstaller.Install()`/`Uninstall()` and, alongside it,
  `AiThoughtsListener` (see below and specs/sender-ai-bridge.md). Both build
  monitors raise a `BuildStateChanged(BuildState, string? projectName)`
  event that `MainWindow.OnBuildStateChanged` turns into `FACE BUILDING`/
  `FINISHED`/`ERROR` plus `MSG Build <project> iniciado!/concluído!/falhou!`
  — the project name is the whole point of the message (`(sourceLabel)` in
  parens is only a fallback for the rare case a source couldn't extract
  one). **Visual Studio's own builds are not covered by either of these**,
  and deliberately don't get a third monitor in this process at all —
  `Brobot.VSExtension` (see repository layout above), a real VSIX loaded
  in-process by `devenv.exe`, reports `VsBuildStarted`/`VsBuildSucceeded`/
  `VsBuildFailed` straight to `AiThoughtsListener` instead, routed through
  the same `OnBuildStateChanged` from `OnAiThoughtReceived`'s own switch.
  This split exists because of a hard lesson, not a preference: an
  *external* process calling into Visual Studio's own DTE/`BuildEvents` —
  first with no protection, then again with a correctly-implemented
  `IOleMessageFilter`, the standard documented COM fix for exactly this
  class of problem — hung Visual Studio itself the instant a build started,
  confirmed live both times. A same-process VSIX has none of that risk
  (its DTE calls are ordinary same-thread calls, the same way any of VS's
  own internal code already talks to itself), which is the whole reason it
  exists as a separate project rather than a fourth `Brobot.Sender`
  monitor. An earlier UI-Automation approach (reading the Output window's
  own rendered text) worked too and carries no such risk either, but needed
  the Output tab to actually be the selected tab to keep working — that
  class (`VisualStudioOutputMonitor.cs`) is kept in the repo but no longer
  wired into this checkbox now that the VSIX is confirmed reliable; running
  both at once was producing two separate, differently-worded messages for
  the same build. See `Brobot.VSExtension`'s own header comment for why it
  hooks `OnBuildProjConfigBegin`/`OnBuildProjConfigDone` specifically
  (per-*project*, firing once per project MSBuild actually processes, each
  with its own name and a direct success bool) rather than the
  solution-level `OnBuildBegin`/`OnBuildDone` pair an earlier version used
  — the solution-level pair has no project name to report at all, and
  `SolutionBuild.LastBuildInfo`'s failed-project *count* is a strictly
  worse signal than a same-project success bool handed to you directly.

  **Git** ("coisas que ocorrem no Git" — commit/merge/checkout/push) is the
  card's other half, added alongside build detection rather than as its own
  checkbox: there's no separate provider choice to make the way Atividade da
  IA's Claude/Codex/Gemini/Cursor picker has, so folding it into one existing
  checkbox (per an explicit product call) was simpler than introducing a
  section header this app's card list has never needed before.
  `GitHookInstaller.cs` sets git's own **global** `core.hooksPath` (not a
  per-repo `.git/hooks` edit) to a folder copied next to the running exe —
  same "applies to every project, not just whichever one happens to be
  open" reasoning `ClaudeCodeHookInstaller` already uses for
  `~/.claude/settings.json` — containing four static POSIX-shell shims
  (`hooks/git-hooks/post-commit`/`post-merge`/`post-checkout`/`pre-push`,
  pinned to LF line endings via `.gitattributes` since a CRLF-mangled
  `#!/bin/sh` shebang fails to execute at all under Git for Windows' bundled
  `sh.exe`, which is what `core.autocrlf=true` would otherwise silently
  produce on checkout). Each shim just calls `hooks/mimo-git-hook.ps1`
  (mirrors `mimo-claude-hook.ps1`'s own socket-write-to-`AiThoughtsListener`
  core almost verbatim) with a fixed `-EventName`, resolved via
  `$(dirname "$0")` so the shim keeps working wherever this folder actually
  lands rather than baking in an absolute path. `post-checkout` filters its
  own third hook argument (`1` for a real branch switch, `0` for a plain
  `git checkout -- file`) before ever spawning PowerShell, so restoring a
  file never fires a git event. **Never overwrites a `core.hooksPath` the
  user already had of their own** — same "claim only if empty or already
  ours" caution `ClaudeCodeHookInstaller` applies to Claude Code's
  `statusLine` entry, just for a config scalar instead of a JSON object:
  `Install()` no-ops if the current value isn't empty and isn't already
  ours, and `Uninstall()` only unsets a value it confirms is still ours.
  There's no client-side git hook for "push succeeded" — `pre-push` is the
  only push-related hook and fires before the network round-trip — so
  `GitPush` can only ever mean "a push just started" (see
  `MainWindow.OnAiThoughtReceived`'s `GitCommit`/`GitMerge`/`GitCheckout`/
  `GitPush` cases for the exact FACE/MSG each one sends); `GitCommit`/
  `GitMerge`, by contrast, only fire once git has already applied them
  successfully — a failed or conflicted one stops before invoking the hook
  at all — so neither needs a `BuildState`-style Started/Successful/Failed
  shape, unlike the build monitors above. This card's checkbox is also what
  starts/stops the shared `AiThoughtsListener` now (previously only
  Atividade da IA's install button did): `MainWindow.
  StopAiThoughtsListenerIfUnused` is the "does anyone still need it" guard
  both features' off-switches run through, so toggling one off never
  silences whichever event source the other one still needs.
