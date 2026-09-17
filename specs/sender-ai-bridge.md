# Brobot.Sender Internals — Atividade da IA / Claude Code Bridge

- **"Atividade da IA"** (a provider `ComboBox`: Claude/Codex/Gemini/Cursor/
  Outro, plus an **Instalar**/**Desinstalar** button — no checkbox) is the
  per-tool-hooks → local endpoint → `FACE`/`MSG` bridge. Unlike every other
  card, this one isn't a live toggle: installing edits the user's real,
  persistent Claude Code config (`ClaudeCodeHookInstaller`, below), so it's a
  deliberate, explicit, reversible action behind its own button rather than
  something that happens as a side effect of a checkbox. The button's own
  label always reflects the *actual on-disk* install state (re-read every
  time, not cached) and is only enabled when "Claude" is selected in the
  `ComboBox` — the other providers are UI/settings-persistence only so far,
  since only Claude Code hooks are actually wired up.
- **`ClaudeCodeHookInstaller.cs`**: its `Events[]` array is the list of
  Claude Code hook events the bridge claims — `SessionStart`,
  `UserPromptSubmit`, `PreToolUse`, `PostToolUseFailure`,
  `PermissionRequest`, `PermissionDenied`, `Notification`, `SubagentStart`,
  `SubagentStop`, `PreCompact`, `PostCompact`, `Stop`, `StopFailure`,
  `Pre`/`PostModelSwitch`, `TaskCreated`, `TaskCompleted`, `CwdChanged`,
  `SessionEnd` — each with an "any tool" `.*` matcher where it's a
  tool-scoped event and none where it isn't. What's *absent* is as
  deliberate as what's there: **`PostToolUse` (success) is deliberately not
  installed** — `PreToolUse` already announced that same call and Core's
  `FACE_OVERRIDE_DURATION_MS` already returns the face on its own, so it
  would double the PowerShell processes spawned per tool call to say nothing
  new. `MessageDisplay`, `FileChanged`, `InstructionsLoaded`, `ConfigChange`,
  `DirectoryAdded` and `TeammateIdle` are out for the opposite reason: they
  fire often enough that MiMo would strobe rather than report.
  `PostToolUseFailure` is the one taken from that family, because a tool
  *failing* is genuinely new information and is the only thing in the whole
  bridge that can legitimately show `ERROR`.
  It edits
  `%USERPROFILE%\.claude\settings.json` — Claude Code's *global* settings, so
  the bridge applies to every project, not just whichever one happens to be
  open. Edits are surgical via `System.Text.Json.Nodes`, never a wholesale
  rewrite: every hook entry this class adds or removes is identified by its
  `"command"` containing `mimo-claude-hook.ps1` plus the specific event name,
  so any other hooks the user already has (their own, or another tool's) are
  left alone, and a matcher-group/event key is only pruned once it's been
  emptied entirely by removing *our* entries. `IsInstalled()`/`Install()` are
  idempotent — re-running `Install()` skips events that are already there
  instead of duplicating them, which also means a partial/broken install
  (e.g. the user hand-deleted one event) self-repairs on the next click
  rather than needing a manual uninstall first. The same click also wires up
  `settings.json`'s top-level `"statusLine"` (see `mimo-claude-statusline.ps1`
  below) — a single object, not an array of installable entries like
  `hooks`, so `Install()` only claims it when it's absent or already ours
  (`IsOurStatusLine`, matched the same way as hook entries — by the
  command containing `mimo-claude-statusline.ps1`), never overwriting a
  statusLine the user configured themselves. This is deliberately
  best-effort: `IsInstalled()` still only checks `hooks`, so a pre-existing
  foreign statusLine can't leave the "Instalar" button permanently unable
  to report success.
- **`hooks/mimo-claude-hook.ps1`** (repo root, copied to Brobot.Sender's
  output directory — see the csproj — so `ClaudeCodeHookInstaller` can point
  a hook command at a real file next to whichever `Brobot.Sender.exe` is
  actually running, not a hardcoded source-repo path): reads the hook's JSON
  off stdin, extracts a short label for whichever event fired, and sends one
  line `EVENTNAME [text]` to `AiThoughtsListener`. Must never fail the hook or
  block Claude Code — every failure path (socket refused, bad JSON, anything)
  is swallowed and it always `exit 0`s — and must never write to stdout,
  since `UserPromptSubmit` hooks have their stdout appended straight into
  Claude's context.
  One script handles every event; *which* events actually fire is
  `ClaudeCodeHookInstaller`'s `Events[]`, not this file's business, so adding
  one is an entry there plus an arm in this switch plus an arm in
  `OnAiThoughtReceived`. What it pulls out per event:
  `PreToolUse` prefers Claude's own `tool_input.description`, falling back to
  a tool-specific summary (file name, search pattern, URL, the raw command),
  then a Claude-tool-name → Portuguese-phrase table (`Bash` → "Executando
  comando..."), then the bare tool name — and prefixes `(agent_type)` when
  the call came from a subagent, the one field that distinguishes "Claude is
  doing this" from "something Claude delegated is doing this".
  `Stop` and `SubagentStop` read **`last_assistant_message`** — the actual
  text just produced — which is why MiMo can now report what he did instead
  of a fixed "Terminei!". `PostToolUseFailure` digs an error string out of
  `tool_response`, whose shape is per-tool (a bare string for some, an object
  with `error`/`stderr`/`message` for others), so all of those are tried
  rather than betting on one. `SessionStart` builds a greeting from `model`
  and the folder name in `cwd`; `Notification` reads `.message`;
  `PermissionRequest`/`PermissionDenied` read `tool_name`;
  `Pre`/`PostModelSwitch` read `to_model`; `CwdChanged` reads `cwd`.
  `Get-FolderName` deliberately returns nothing for the **home directory and
  for a drive root**, so the greeting drops the place rather than naming it:
  switching accounts starts a session in the home directory, which produced
  "Bora trabalhar em Gabriel!". Its one subtlety is that the trimmed form is
  used only for comparing — handing a trimmed `"C:"` to `Split-Path` makes it
  a drive-*relative* path, which resolves against the hook process's own
  current directory and turned the drive root into whatever folder that
  happened to be. A real bug, fixed once.
  **Switching accounts has no hook event at all** — not even indirectly.
  Claude Code's docs mention an `auth_success` *type* on the `Notification`
  event's matcher, and an earlier version of this bridge tried detecting a
  switch by scanning the payload for that value; measured against the real
  desktop app, an actual account switch fired no `Notification` whatsoever,
  only the same `SessionEnd`/`SessionStart` burst any session restart
  produces. That attempt was removed rather than left in as speculative dead
  code — see `ClaudeCodeAccount.cs` in Brobot.Sender for how this is detected
  instead (by reading the signed-in account straight out of the user's own
  `~/.claude.json`, compared against the previous `SessionStart`'s).
  `model`/`to_model` are read as *either* a bare id string or a
  `{id, display_name}` object, since the docs only say "the model" and the
  shape differs by event.
  Every extracted string goes through `ConvertTo-SingleLine` before being
  sent. That isn't cosmetic: the wire format is one line per event, and
  `last_assistant_message` is real prose — multi-line, markdown, sometimes
  with fenced code in it — so one raw newline would split a single event into
  two garbage lines on the listener's side. `Get-FirstSentence` then takes
  the first *sentence* rather than the first 100 characters, because a hard
  cut lands mid-word and reads as truncation while one whole sentence reads
  as MiMo actually saying something. `Limit-Length` is still the backstop
  right before sending (a long sentence is still possible) — it caps at 100
  chars and appends **three literal periods**, not a single `"…"` character:
  Core reads the wire byte-at-a-time and both fonts (`Font5x7`/the physical
  display's own) work in single-byte Latin glyphs, so a multi-byte UTF-8
  ellipsis arrived as three bytes with no glyph mapping and drew as
  invisible gaps instead of a visible "this was cut" cue — a real bug,
  fixed once. `Personality::TypedMessage::set` (`Personality.cpp` above)
  does the same three-periods truncation independently, as a backstop for
  any message reaching Core's own 255-char-per-tier capacity from a source
  other than this script (weather alerts, media titles, a raw `MSG` sent by
  hand).
- **`hooks/mimo-claude-statusline.ps1`** (same repo root / copy-to-output /
  path-resolution setup as the hook script above): registered as Claude
  Code's `statusLine` command, not a `hooks` entry — a genuinely different
  contract, not just another event. It receives a much richer JSON payload
  (`model`, `cost`, `context_window`, ...) than any hook gets, and — unlike
  `mimo-claude-hook.ps1`, where writing to stdout is actively forbidden —
  this script's own stdout *is* what Claude Code renders as the terminal's
  status line, so it always prints a one-line summary no matter what else
  happens, alongside forwarding the same text to `AiThoughtsListener` as a
  `ContextUsage` event. Computes "`N` tokens gastos na requisicao. `X`% do
  contexto utilizado" from `context_window.current_usage` (the last
  response's `input_tokens + output_tokens + cache_creation_input_tokens +
  cache_read_input_tokens`) and `context_window.total_input_tokens /
  context_window.context_window_size`. Two deliberate departures from what
  the field names suggest, both per Claude Code's own docs, not a bug here:
  `context_window.total_output_tokens` is *not* a running session total
  despite the name — it's just the most recent response's output tokens —
  so it's not used at all; and `total_input_tokens` (tokens currently
  loaded in the context window, which resets after a `/compact`) is what
  gets treated as "session total" for the percentage, per an explicit
  product call, since Claude Code exposes no field that's a true
  since-session-start running sum. `current_usage` is `null` before the
  first API call and again right after a `/compact`, so every field read
  is guarded and falls back to `0` — the terminal's status line must never
  go blank because of a null here, and neither must the MiMo side.
  It sends **two** lines per invocation, not one, because they answer
  different questions and Core treats them differently: `ContextUsage <text>`
  (the same sentence printed in the terminal, shown as an ordinary `MSG` —
  unchanged, and what `DEFAULT`/`MI2MO2` get since they have no panel to draw)
  and `AiStats <ctx%> <costCents> <rate5h%> <rate7d%> <model>`, which
  `MainWindow` forwards verbatim as PROTOCOL.md's `AISTATS`. The numbers come
  from `context_window` (the percentage deliberately recomputed here rather
  than read from the pre-calculated `context_window.used_percentage`, so the
  figure in the terminal and the figure on MiMo's screen are literally the
  same variable and can't disagree), `cost.total_cost_usd` (converted to
  integer cents, since the wire carries integers only),
  `rate_limits.five_hour`/`seven_day.used_percentage`, and
  `model.display_name`. Every one of those is absent on some plan, setup or
  moment, so each falls back to `-1` — the same "no source for this"
  convention `STATS` already uses, which Core draws as `--`. Each line goes
  down its own connection: `AiThoughtsListener` reads exactly one line per
  connection and closes.
- **`AiThoughtsListener.cs`**: a plain `TcpListener` on `127.0.0.1:5591`
  (`MainWindow.AiThoughtsPort`), not `HttpListener` — `HttpListener` needs
  either Administrator or a `netsh` URL ACL reservation to bind a prefix on
  Windows, even a loopback one, which a tray app shouldn't require. Wire
  format is one line per event, `EVENTNAME optional free text...` (mirrors
  PROTOCOL.md's own `FACE`/`MSG` line shape, just inbound instead of
  outbound). Connections are accepted on one thread and **read on a single
  worker thread**, in accept order, via a `BlockingCollection` — never on
  thread-pool threads, which is what it used to do. Order is load-bearing
  here and the pool destroyed it: a burst of hooks is a burst of *separate
  processes*, and with one pool thread per connection two of them were read
  and raised concurrently, so the order `MainWindow` applied them in had
  nothing to do with the order they arrived in. Measured against the pre-fix
  code, even 300 strictly sequential connections came out reordered, and a
  connection that was accepted first but slow to write was reliably raised
  *second*. A session start is exactly that burst — `SessionStart`, the
  status line, and often the previous session's `SessionEnd`, all within a
  few hundred ms — and a `SessionEnd` applied after a `SessionStart` wipes
  the greeting that just went up, which is what "it writes it and then sends
  a command to clear it" looked like from the outside. This was a real bug,
  fixed once. A `ReceiveTimeout` (1s) is what keeps a hook that connects but
  never writes from wedging the queue behind it, now that there's only one
  reader. `MainWindow.OnAiThoughtReceived` logs every event it applies, with
  a timestamp, to `%AppData%\Broboti-events.log` (`LogAiEvent`, capped at
  256KB and self-truncating) along with the ones it deliberately drops —
  MiMo only ever shows the *result* of this race, one message at a time, so
  without that log "the greeting vanished" looks identical whatever caused
  it. And
  `MainWindow.OnAiThoughtReceived` maps event names to Core commands:
  `SessionStart` → `FACE HAPPY` + a greeting (the one AI event that's
  unambiguously good news), `UserPromptSubmit` → `FACE THINKING` +
  `MSG Pensando...`, `PreToolUse` → `FACE READING` (+ `MSG <text>` if the hook
  sent one), `SubagentStart` → `FACE THINKING` too (a subagent working is
  still the AI thinking — what changed is the message, not the state, so it
  shares the expression rather than inventing one), `PreCompact` →
  `FACE THINKING` + "Organizando a memoria...", `PostCompact` →
  `FACE FINISHED`, `Stop` → `FACE FINISHED` + **the first sentence of
  `last_assistant_message`** (falling back to "Terminei!" for a turn that
  ended with no text), `SessionEnd` → `FACE NEUTRAL` + clear `MSG` + clear
  `AISTATS`.
  Three events map to `FACE ERROR` — `PostToolUseFailure`, `PermissionDenied`
  and `StopFailure` — and they are the *only* things in the whole app that
  ever send it: Core's `Expression::FAILED` and `Buzzer`'s three-trill
  "uh-oh" cue both existed with nothing on the AI side able to trigger them
  until these events were installed.
  `PermissionRequest` is the one AI event that maps to `NOTIFY` rather than
  `FACE`/`MSG`: Claude has stopped and can't continue until you look at the
  terminal, which is exactly the "whole screen, 10s, expires on its own"
  case the notification tier exists for — a `MSG` would queue politely behind
  whatever is already up. It carries `READING`, whose "olha aqui" chirp is
  already the sound for precisely this, rather than `ERROR`: asking
  permission isn't a failure.
  **An account switch is detected inside the `SessionStart` case itself**,
  not as a separate event — see the hook script's note above on why nothing
  in the hook payload can signal it. `ClaudeCodeAccount.TryRead()` reads
  `oauthAccount.accountUuid`/`displayName` out of `~/.claude.json` (a
  different file from the `~/.claude/settings.json`
  `ClaudeCodeHookInstaller` writes — this one is never written, only read),
  and `SenderSettings.LastClaudeAccountUuid` remembers the account seen on
  the *previous* `SessionStart`. A mismatch swaps the greeting to "Conta do
  Claude trocada para `<nome>`!", which wins outright over the generic
  "Bora trabalhar em `<pasta>`" one — a switch is more specific, and it
  lands you in the home directory anyway, where there's no project name to
  offer instead. The very first `SessionStart` after installing this (an
  empty `LastClaudeAccountUuid`) deliberately does *not* count as a switch —
  there's nothing to have switched from — it only starts recording from
  there. The recorded UUID is written immediately, the same "fact about the
  world, not a preference" treatment `PersistDiscoveredAddress` already gives
  MiMo's IP, rather than waiting on "Salvar configurações".
  `Notification`, `SubagentStop`, `ContextUsage` (from
  `mimo-claude-statusline.ps1`, not a hook — see above) and the housekeeping
  group (`Pre`/`PostModelSwitch`, `TaskCreated`, `TaskCompleted`,
  `CwdChanged`) all send `MSG <text>` only, with no `FACE` change — a
  notification isn't itself an expression, a subagent finishing isn't the
  *main* agent finishing, and the rest are worth saying but not worth a face.
  `AiStats` is a straight passthrough to `AISTATS`; this app has no opinion
  about how Core draws it, same as every other command it sends. Because
  `AISTATS` is persistent on Core (nothing times it out, exactly like
  `WEATHER`/`TIME`/`STATS`), `_aiStatsActive` tracks it the way
  `_aiThoughtFaceActive` tracks sticky THINKING, and `ClearAiStatsIfActive`
  sends an argument-less `AISTATS` on `SessionEnd` and on uninstall — without
  it the last reading sits frozen in the AI tab forever, announcing a session
  that ended.
  `SendAiMessage` exists because a hook's text is optional by design and
  `MSG` with no argument means *clear the message* on Core, so forwarding a
  null through would silently wipe the screen instead of saying something.
  It's also the single choke point every AI `MSG` goes through, which is what
  makes `_aiMessageHoldUntil` a one-line rule rather than a check repeated at
  a dozen call sites. That hold exists for exactly one event:
  **a session starting is the only moment three messages fire within a few
  hundred milliseconds of each other** — `SessionStart`'s greeting,
  `UserPromptSubmit`'s "Pensando...", and the status line, which Claude Code
  runs once on session start whether or not a prompt was submitted — so the
  greeting was reliably being overwritten mid-word, about a second before it
  finished saying which project had been opened. `HoldAiMessagesWhileTyping`
  drops AI messages until the greeting has finished typing itself in; the
  window is computed from the greeting's own length against
  `CoreTypingCharIntervalMs` (40ms, hand-mirrored from `Face.h`'s
  `TYPING_CHAR_INTERVAL_MS` the same way `IDisplay` and `Font5x7.CharAdvance`
  already are) rather than fixed at a second, because a second isn't enough —
  the usual greeting is 31 characters, so it needs ~1.24s. Nothing else is
  held: everywhere but here, a message replacing an older one promptly is the
  wanted behaviour, since a tool description lagging behind the tool actually
  running would be worse than one cutting the previous description short.
  `NOTIFY` bypasses the hold — it's a different tier that Core arbitrates
  above everything anyway. `SessionEnd` is the opposite case and is
  deliberately *dropped* inside the window rather than let through: a
  `SessionEnd` arriving within a second of a greeting belongs, by
  construction, to the session that just ended to make room for this one
  (starting a new conversation, or `/clear`, ends the old session and begins
  a new one as two unordered hook processes), so honouring it would clear a
  greeting that is still typing itself in. A session that genuinely ends that
  fast loses only a tidy-up it gets again on the next `SessionEnd`. THINKING is Core's one remaining sticky
  *foreground* expression (see PROTOCOL.md/Personality.cpp), so
  `_aiThoughtFaceActive` tracks it the same way `_mediaFaceActive`/
  `_gameFaceActive` track their own sticky *background* expressions —
  uninstalling (or the app exiting) while THINKING is showing explicitly
  sends `FACE NEUTRAL`/empty `MSG` (never `FACE IDLE` — that clears the
  other tier), or Brobot would be stuck glitching forever. `MainWindow`
  starts this listener
  whenever `ClaudeCodeHookInstaller.IsInstalled()` is true — on launch
  (`RestoreSettings`) and right after a successful Install — rather than
  gating it behind any checkbox state.
- **`SenderSettings.cs`**: a small JSON POCO persisted to
  `%AppData%\Brobot\mimo-sender-settings.json` — checkbox states, the chosen
  AI provider, and Core's TCP host/port (no Serial fields — see [connection.md](connection.md)).
  Checkboxes still take effect **immediately** when toggled regardless of saving (same
  `Checked`/`Unchecked` handlers as before); only *remembering that across a
  restart* requires clicking "Salvar configurações". On startup,
  `RestoreSettings()` reconnects to Core and re-checks whatever was saved,
  which replays the exact same handlers a manual click would — no separate
  "apply settings" code path to keep in sync with the live checkbox logic.
