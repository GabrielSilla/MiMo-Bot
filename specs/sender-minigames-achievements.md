# Brobot.Sender Internals — Mini Games and Conquistas Tabs

- **Mini Games tab**: one card per minigame (Pong, Batalha RPG), each with
  its own JOGAR/BATALHAR button — no umbrella entry point in front of them
  any more. This used to be a second, tab-less "page" (`GamePickerGrid`) an
  Anti-Stress card's own button swapped in via plain `Visibility` toggling
  (`ShowGamePicker`/`ShowMainChecklist`); now it's just this permanent tab,
  so there's no entering/leaving to track and those two methods are gone
  entirely. `PlayPongButton_Click`/`PlayRpgButton_Click` are what actually
  send `FACE HAPPY` + a greeting `MSG`, wait 5s, then `PONG`/`RPG START` and
  install a `GlobalKeyboardHook` — structurally identical copies of each
  other rather than a shared helper, the same "only two of these, copying
  the shape is simpler than generalizing it" call this file already makes
  for `ClearGameFaceIfActive`/`ClearMediaFaceIfActive`; worth pulling into
  one method if a third minigame shows up. `PARAR` (`StopGameButton`, née
  `BackFromGamesButton`/`VOLTAR` — renamed once it stopped navigating
  anywhere) is always visible on the tab and handles all three ways of
  giving up — nothing picked yet (a no-op, both hooks/timers already null),
  still waiting out the 5s greeting, or a round/battle already running (the
  only case that actually sends a `PONG`/`RPG STOP`) — by checking which
  hook field is non-null. A game ending any other way (naturally, or via
  Escape) calls `StopPongGame`/`StopRpgBattle` (renamed from
  `StopAntiStressGame`), which write the result ("Última pontuação: 7",
  "Vitória!", ...) straight onto that game's own `PongPickerStatusText`/
  `RpgPickerStatusText` — there's no separate checklist-side status label to
  hand it off to any more (the old `AntiStressStatusText` existed
  specifically because the picker's per-game lines were about to disappear
  along with the page; that's no longer true, so the simpler direct write
  replaced it).
  `GlobalKeyboardHook.cs` (one of this app's two P/Invoke consumers — see
  `TeamsNotificationWatcher.cs` below for the other) is a system-wide
  `WH_KEYBOARD_LL` hook: the player is watching MiMo's own screen while
  playing, not this window, so arrow/Enter/Escape have to reach Core
  regardless of what has focus on the PC. Installed from the UI thread, its
  callback then runs synchronously on that same thread's message pump — the
  one background-originated callback in this app that does *not* need
  `Dispatcher.Invoke` to touch UI/`_connection`, unlike every monitor
  below. It never suppresses a key (always calls `CallNextHookEx`), a
  deliberate simple default. Left/Right/Enter only fire on the actual
  press→release transition (Windows repeats `WM_KEYDOWN` while a key is
  held); Escape does not bother with that dedupe, since
  `StopPongGame`/`StopRpgBattle` are idempotent and firing twice from
  a held Escape is harmless.
- **Conquistas tab**: 10 fixed achievements (`AchievementCatalog.All`), each
  tracked against a signal this app already observes elsewhere for its own
  reasons — no new OS-level sensor needed for any of them. Two ideas from the
  original brainstorm (a "deep focus"-style window-switch timer, a PC-uptime
  "marathon") were dropped for exactly that reason: nothing here watches
  which window has focus, and while the OS's own boot time is cheap to read
  once (`Environment.TickCount64`, used for Early Bird below), a *running*
  uptime accumulator would need an always-on timer this app has no other
  reason to keep.
  `AchievementMonitor` (persisted via `AchievementStore` to
  `%AppData%\Brobot\achievements.json`, same neighborhood/best-effort error
  handling as `SenderSettings`/`GameMonitor`'s own cache) is a plain class
  with one method per signal, called from the exact spot in `MainWindow` that
  already reacts to that signal for its own reason — it adds bookkeeping
  alongside that call, it doesn't own or replace it:
  `OnConnected` (from `UpdateConnectionStatus`'s `connected && !_wasConnected`
  branch) for **First Contact** and **Early Bird**; `SetGameActive`/
  `SetMusicActive` (from `OnGameChanged`/`OnNowPlayingChanged`, and the
  Jogos/Mídia checkboxes' own unchecked branches, so toggling a monitor off
  mid-session can't leave a stale "active" flag stuck true) accumulate toward
  **One More Game**/**Audiophile**; `OnAiActivity` (one call at the top of
  `OnAiThoughtReceived`, regardless of which hook event it is — a heartbeat,
  not per-event logic) toward **AI Overload**; `OnRpgVictory` (from
  `OnFrameReceived`'s `RPG OVER VICTORY` case) counts toward **Victory
  Royale**; `OnBreakReminderSent` (from `SendBreakReminder`) toward **Break
  Taker**; `OnThemeSelected` (from `TemaComboBox_SelectionChanged`, keyed on
  `CoreTheme` — `DEFAULT`/`MATRIX`/`MI2MO2`/`MI84`) tracks **Identity
  Crisis**'s "used every theme" set.
  `Tick(connected)` is what drives every duration-based accumulator —
  **Coffee Machine** (4h connected in a day), **One More Game** (3h with a
  game active), **Audiophile** (24h lifetime with music active), plus
  re-checking **Night Owl** (wall clock before 05:00, connected) and **Early
  Bird** (OS boot time before 07:00) on every call. It's called from
  `UpdateConnectionStatus`'s existing 200ms poll rather than a timer of its
  own, since that poll already knows `connected` at every tick. None of the
  duration criteria are a real activity/idle detector — nothing here watches
  mouse/keyboard input, so "Coffee Machine" really measures "MiMo was
  connected", not "you were at the keyboard"; honest enough for a
  Tamagotchi-style nudge, not a timesheet. Daily accumulators
  (`TodayConnectedSeconds`/`TodayGameSeconds`/`TodayAiActiveSeconds`) reset
  the moment `AchievementProgress.Date` no longer matches today rather than
  prorating a session that spans midnight; `Tick`/`OnAiActivity` also guard
  against a gap over 5 minutes (system sleep, a clock change) crediting that
  whole gap to whatever was active before it.
  `TryUnlock` saves immediately the instant something actually unlocks (an
  unlock is a fact worth persisting right away, same reasoning
  `MimoDiscovery`'s `PersistDiscoveredAddress` already follows) but the
  accumulators themselves are only flushed to disk once a minute
  (`MaybeSave`) — `Tick` fires every 200ms and `OnAiActivity` on every AI hook
  event, and writing the file on every one of those would be pure waste.
  The tab's 10 cards are built in code (`MainWindow.BuildAchievementCards`),
  not hand-authored XAML like every other card in this app: they're all the
  same shape (icon badge, name, description-or-quote, status), so a loop over
  `AchievementCatalog.All` plus a small dictionary of the four elements each
  card needs to update later (`RefreshAchievementCard`) replaces what would
  otherwise be ten near-identical XAML blocks and forty named fields — the
  one departure from this app's usual all-XAML convention, and worth it only
  because there's nothing to customize per card. A locked card shows a 🔒 in
  place of its real emoji and its criterion text (what to still aim for), at
  reduced opacity; the moment `AchievementMonitor.Unlocked` fires
  (`OnAchievementUnlocked`), the card swaps in the real emoji, the flavor
  quote, the unlock date, and full opacity.
  **Unlocking also reaches MiMo's own screen**, reusing Core's existing
  top-priority `NOTIFY` tier rather than adding any new protocol command or
  firmware feature — `NOTIFY HAPPY Conquista desbloqueada: <NOME> - <frase>`
  gets every behavior every other notification already has for free
  (full-screen, outranks even AI activity, auto-clears after 10s; see
  PROTOCOL.md). `HAPPY` for the same reason `SessionStart`'s greeting uses
  it: this is unambiguously good news. `Achievement.Emoji` is WPF-only —
  Core's bitmap `Font5x7` has no glyph for any of these, so the text sent
  over `NOTIFY` never includes it, only the plain-text name and quote (both
  written with real Portuguese diacritics, which Core's font *does* support).
