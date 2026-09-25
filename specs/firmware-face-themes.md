# Firmware Internals — Face.cpp Themes (MATRIX / P2M2 / PEEMO84)

Continues [Face.cpp core rendering](firmware-face-core.md) — same file, same
`Face::render`, just the whole-frame theme reskins broken out on their own.

- **`Theme::MATRIX`** (Sender's Tema card, "Peemo Matrix" entry, `THEME MATRIX`/`THEME
  DEFAULT` — named `CLASSIC` in the `Theme` enum, not `DEFAULT`, since
  `<Arduino.h>` `#define`s `DEFAULT` as a macro, same class of problem as
  `Expression::FAILED` not being named `ERROR`) is a whole-frame reskin
  layered on top of everything above, not a new `Expression`: `Face::render`
  composes a `RecoloringDisplay` decorator (recolors every non-black draw
  call to a fixed green, leaving actual black — every "cut a gap" punch-out
  — alone so rounded corners/spine gaps/d-pad cutouts still read as hollow)
  the same way it already composes `DimmingDisplay` for `SLEEPING`, with
  `RecoloringDisplay` innermost so a simultaneous `SLEEPING` dims the theme's
  green instead of un-recoloring it back to teal. While `MATRIX` is active,
  every *expression* corner icon is suppressed (the music-note-through-
  gamepad icons, even Sleeping's "Z Z Z") — their info now lives in the
  console log below instead, and their usual Y-range would otherwise
  collide with it. The one exception is `COFFEE`'s cup: unlike every other
  expression, `COFFEE` already repositions the eyes themselves (small,
  pinned to the left — see below) specifically to make room for the cup,
  regardless of theme, so suppressing just the icon while still applying
  that repositioning left the eyes shrunk into the corner for no visible
  reason on screen — a real bug, fixed once — rather than a case where
  suppressing the icon and letting the log stand in for it actually made
  sense the way it does for every other expression. Since `COFFEE`'s
  eyes+cup layout sits inside the log's own region rather than tucked below
  it, `Face::render` skips the log there entirely instead of drawing over
  (or getting drawn over by) the cup — same "swap it for something else
  while this expression is active, no extra state needed" idea `THINKING`'s
  rain already uses (see below); the log reappears on its own the instant
  `COFFEE` clears. `drawWeatherBadge`/`drawClockBadge` are *not* suppressed
  either — Hora/Clima stay fixed at the top exactly as in `CLASSIC`, so the
  log starts below that strip (`MATRIX_LOG_TOP_Y`, matching
  `CORNER_ICON_Y_SHIFT`) instead of overlapping it. The eyes — still
  expression-shaped, but ~36% smaller than `CLASSIC` (`MATRIX_EYE_SIZE`/
  `MATRIX_EYE_GAP` — two successive ~20% reductions, tuned by eye rather
  than a single formula; at full size, pinned to the bottom edge, they read
  as too large/heavy for this theme) — get pinned to the bottom of the frame
  instead of the usual upper-middle spot (`COFFEE`'s own left-pinned layout
  still takes precedence over Matrix's bottom-center one, same "coffee's
  layout wins" precedence order the geometry code already had). The freed-up
  top-middle of the frame (below the badge strip) renders `Personality`'s
  own scrolling log (`_log`, `MATRIX_LOG_LINES` = 6 entries,
  `MATRIX_LOG_LINE_CAPACITY` = 74 chars each — sized so one entry can
  word-wrap across a full 3 on-screen lines, the same visible-line budget
  CLASSIC's own message box gives a single message — in `Face.h` since both
  `Personality` and `FaceState` need the same capacity) — a plain
  shift-and-append array, not a wraparound ring buffer, since the capacity
  is tiny and this keeps `Personality` handing out a simple 0..count
  chronological list with no index math. `Personality` only stores the raw
  (unwrapped) entries; `Face.cpp`'s `drawMatrixLog` is the one that greedy
  word-wraps each entry to fit the screen width (same algorithm
  `drawWrappedMessage` already uses for the normal message box) and draws
  the `> ` prompt prefix only on an entry's *first* physical line — a
  wrapped continuation is still the same message, not a new prompt, so it
  draws flush left with no prefix. This replaced an earlier version that
  drew each raw entry as exactly one unwrapped line: any entry longer than
  the screen width just ran off the right edge and was never seen at all,
  rather than merely getting cut off — a real bug, fixed once. Wrapping is
  purely `Face.cpp`'s concern, not `Personality`'s, matching the project's
  "PC-side/render layer renders, Core's logic layer decides" split one level
  down. Physical (wrapped) lines scroll off the *top* of the log, oldest
  first, once there are more than fit above the pinned eyes
  (`MATRIX_LOG_BOTTOM_GAP` below `MATRIX_EYE_SIZE`'s position) — the same
  "oldest visible line drops off" idea `drawWrappedMessage` already uses for
  a single message, just applied across the whole log instead of one entry.
  While `state.expression == THINKING`, `Face::render` calls `drawMatrixRain`
  into this same region instead of `drawMatrixLog` — a grid of columns
  (`MATRIX_RAIN_COLUMN_SPACING_PX` apart) each showing a short
  `MATRIX_RAIN_TRAIL_LEN`-character run of random `MATRIX_RAIN_CHARS` that
  rises (decreasing row) and wraps back in from the bottom edge once it
  exits off the top, each column at its own hashed speed/phase so they don't
  move in lockstep — same "no persistent state, driven purely off nowMs"
  approach `drawEyeGlitch` already uses, via its own `matrixRainHash` (kept
  separate from `glitchHash` so the two effects' timings don't correlate).
  Because this only swaps what gets drawn into the log's region for exactly
  as long as `resolveExpression` keeps reporting THINKING, there's no
  animation-start/stop state to track: the log reappears on its own, with
  no special-casing, the instant THINKING clears and rendering falls back to
  `drawMatrixLog` again next frame.
  A **third tab, MONITOR**, holds the game being played and — uniquely among
  the tabs — draws Peemo's machine stats under it (`drawMatrixMonitor`, see
  `FaceState::hasStats` and PROTOCOL.md's `STATS`). A game's "Jogando X" used
  to land in the media tab beside music and video; it moved here because it's
  the one entry with numbers to show beneath it. `drawMatrixLog` now returns
  the y it stopped at, purely so the monitor tab can start its stat lines
  below a game name of whatever height it happened to wrap to, and stops
  early rather than drawing over the eyes if a long name eats the room.
  Which tab is live still follows what the face itself is doing, with no
  extra state: PLAYING shows MONITOR, other background expressions show
  MEDIA, anything else shows AI.
  This log only ever carries the
  "prompt"-style messages (AI activity, media, games, Pausa, bedtime) — it's
  unchanged by this; the messages that do show there render exactly as
  before. It deliberately does *not* carry a time/weather line — Hora/Clima
  already render as their own persistent badges (see `Face::render` below),
  so a periodic log entry restating the same values would just be noise now
  that both are on screen at once; an earlier version of MATRIX did log a
  once-a-minute `"HH:MM - tempC - condicao"` line here, back when the
  badges were suppressed in this theme and the log was the only way to see
  that info, but it was removed once the badges came back.
  `Personality::pushLogLine` is called from exactly two places: once inside
  `onMessageCommand` (covers every message that already flows through there
  — AI hook text, media "now playing", "Jogando X", weather alerts, Pausa's
  coffee reminders — with a single call site, since the log doesn't care
  which tier a message is headed for) and once for each freshly-picked
  bedtime message.
  **`Theme::P2M2`** (Sender's Tema card, "Peemo P2-M2" entry, `THEME
  P2M2`) is a bigger departure than MATRIX: instead of reskinning a face,
  it replaces the whole frame with a close-up of R2D2's dome plate —
  `drawP2M2Plate` (off-white plate via `clear`, navy inset panels, silver
  vent) then `drawP2M2Lens` then `drawP2M2LogicDisplay`, back to front,
  the same layering `drawMessageBox`/`drawWrappedMessage` already use.
  Every theme-specific color lives in its own `P2M2_*` palette rather
  than reusing `EYE_R/G/B`, because in this theme nothing is "the eye
  color".
  **This design is the second attempt, and the first one's failure is worth
  recording so it isn't retried**: the original P2-M2 was a face — a big
  red radial-gradient eye on the usual black background, on a blue band.
  It was fully built and working (single eye, pupil-shift look-around,
  dim-to-blink) and still read as HAL 9000 / a Cylon rather than R2D2,
  because R2 is predominantly *white* with navy accents and its main eye is
  *black*, not a glowing red lamp. A big red glow on black is the wrong
  silhouette no matter how well the details are executed. What fixed it was
  inverting the ground — light plate filling the frame, navy panels, black
  lens — not tuning the red. The whole `drawEyeRadialGradient` /
  `P2M2_PANEL_*` / pupil-offset machinery from that version was deleted;
  don't reintroduce a glowing eye here.
  The consequence for animation is that **the lens carries no expression at
  all**: it's a fixed black disc that never blinks, squints, moves or
  changes color. Two things stand in for that, both mirroring how the real
  droid works — its eye is a static piece of glass and all its emoting
  happens in the logic panels:
  (a) **look-around slides a white glint across the lens**
  (`P2M2_GLINT_*`) instead of moving the eye — on featureless black
  glass a travelling reflection is the only available cue that the lens
  turned. The glint rests up-and-left of center, which is why
  `Personality.cpp` excludes exactly one look direction in this theme (see
  `LOOK_DIRECTIONS_NO_UP_LEFT` there); and
  (b) **the logic display — the small round lamp right of the lens — carries
  every expression**, including the ordinary blink (it switches off and
  back on, since a black lens has no light to close). `Face::render`
  resolves its color and brightness in one small block before the dispatch:
  THINKING → `p2m2ThinkingDim` (an irregular stutter, two nested time
  scales — a coarse "burst" hashes the flicker rate, each slot within it
  hashes to lit/nearly-out/half-lit — so it never settles into a mechanical
  rhythm), FAILED → `p2m2ErrorDim` (three flashes, then holds *lit*, not
  dark: FAILED outlives the flashes by `FACE_OVERRIDE_DURATION_MS` and a
  dark lamp for the remainder would read as "asleep"), FINISHED → green,
  SLEEPING → off. FAILED is the one bounded animation in this whole
  codebase — everything else (glitch, Matrix rain, coffee steam) loops off
  `nowMs` forever and needs no start time — which is why `FaceState` grew
  `expressionStartedMs` (set by `Personality::update` when
  `_renderExpression` changes) purely for it.
  P2M2 also opts out of the whole-frame `DimmingDisplay` on SLEEPING
  (there, sleeping is specifically the lamp going out with the plate,
  badges and message all at full strength), suppresses SLEEPING's "Z Z Z"
  and COFFEE's cup (COFFEE there is message-only), and splits what every
  other theme keeps as one color: weather/clock badges are navy (fixed
  info printed on the plate; the light plate washes out CLASSIC's teal),
  while the corner icons (music/play/book/gamepad/coffee) are the lamp's
  red (transient status lights) — hence `Face::render`'s separate
  `eyeR/G/B` vs `iconR/G/B`. The message box stays CLASSIC's dark grey with
  white text: an earlier near-white bubble blended straight into the light
  plate.
  **Aurebesh translation effect** (`drawWrappedMessageP2M2`): each
  character is drawn individually rather than a line at a time, because
  each sits at a different point in its own reveal age — derived from
  `FaceState::messageTypingStartedMs` plus its index times
  `TYPING_CHAR_INTERVAL_MS` (which moved to `Face.h` so `Personality` and
  `Face` can't drift on it). For `AUREBESH_HOLD_MS` after being revealed a
  character draws in Aurebesh *red*, then flips to Latin *white* — font and
  color switch together on that same per-character clock, so the line shows
  a visible "decoding wavefront": already-resolved white at the start,
  still-alien red at the end. The Aurebesh red is deliberately lighter than
  the lamp's own red — at 5x7 against the dark box the deeper red loses the
  thin strokes. Fonts were generated from a real `Aurebesh.otf`, not
  hand-drawn, by rasterizing each glyph large and reducing each cell to the
  *darkest* source pixel rather than a smooth resize (a plain resize
  anti-aliases thin diagonals like the digit `7` away to nothing before
  thresholding). Both sides carry their own copy in their own format —
  `AurebeshFont.cs` (5x7 column-major, Simulator) and
  `AurebeshGFXFont.h` (Adafruit `GFXfont`, firmware) — and both cover only
  `A-Z`/`0-9`, falling back to Latin for anything else. `TextFont`
  (`IDisplay.h` / `IDisplay.cs`, plus a token on the wire's `TEXT` line)
  is what carries the choice; `ST7735PhysicalDisplay::drawText` switches
  fonts per character and shifts its cursor down by the glyph height while
  Aurebesh is active, since a custom `GFXfont` draws from the text
  *baseline* while the built-in font draws from the top-left corner —
  getting that wrong misaligns Aurebesh against the Latin characters beside
  it.
  **`Theme::PEEMO84`** (Sender's Tema card, "Peemo-84" entry, `THEME PEEMO84`) is a
  1984 amber-CRT terminal, and it is the mirror image of P2M2's move:
  where that theme replaces the *face*, this one replaces the *frame*. The
  top two thirds become fixed terminal chrome — a `PEEMO SYSTEM v2.6` header,
  a status row, two rules and a tab bar — and MATRIX's own small
  bottom-pinned eyes sit underneath, still expression-shaped. Everything is
  drawn in one amber (`PEEMO84_INK_*`, 255/176/0) on black, with a darker amber
  (`PEEMO84_DIM_*`) for chrome only: rules, labels, unlit bar cells.
  It deliberately **does not** use `RecoloringDisplay` the way MATRIX does.
  That decorator flattens every non-black color to a single value, and this
  theme needs those two levels, so colors are threaded through explicitly,
  the way P2M2 already does. The one thing that trips over this: the
  corner icons take `iconR/G/B`, which defaulted to CLASSIC's teal — PEEMO84
  suppresses every icon except COFFEE's cup, so the cup came out as the
  single non-amber object on an otherwise monochrome screen. A real bug,
  fixed once, and the reason `iconR/G/B` now has an PEEMO84 arm of its own
  rather than sharing `eyeR/G/B`.
  The log and the tabs are **MATRIX's, reused whole** — same
  `FaceState::logLines`, same `LogTab`, same `Personality::pushLogLine`,
  same `drawMatrixLog` (which grew position/color parameters for this; every
  MATRIX call site passes exactly the constants it used to read directly, so
  that theme's output is byte-identical). This is what made the SDD's
  "IA activity must always win the tab" requirement cost *zero* new logic:
  `Personality::currentState` already derives the tab from what's rendering,
  and foreground beating background in `resolveExpression` already produces
  exactly that priority. Anything else would have been a second copy of a
  rule that already existed.
  Where it does diverge from MATRIX:
  **Hora/Clima are text, not badges** — both readings share one row
  (`TIME 17:42  RAIN 18C`, 20 chars at `CHAR_ADVANCE_PX`) and
  `drawWeatherBadge`/`drawClockBadge` are suppressed, since drawing the
  pictograms as well would print the same information twice in a strip the
  header already occupies. The condition prints as the *wire protocol's own
  `WEATHER` token* (`CLEAR`/`CLOUDY`/`RAIN`/...) rather than a prettier
  second vocabulary that could drift from what's actually being sent. No
  degree symbol — `Font5x7` has none, and `drawWeatherBadge` already prints
  a bare `18C` for the same reason.
  **THINKING doesn't use the glitch eyes.** Here it's carried by the eyes
  flickering (`p2m2ThinkingDim`, reused as-is — it was never really
  P2M2-specific, just the first place that needed an irregular stutter)
  plus a `>THINKING_` prompt line that types itself out, erases and repeats,
  driven purely off `nowMs` like every other looping effect here. That
  prompt takes **only the last content row**, with the log still scrolling
  above it — which is the whole reason the `PreToolUse` tool labels
  ("Executando comando...") stay visible instead of being hidden behind the
  animation, as they would have been had it claimed the whole region.
  **Both sleep states type a prompt of their own**, `>Z..Z..Z...Z.._`, off
  the very same `drawPeemo84TypedPrompt` the THINKING row uses — it takes the
  text and the pacing as parameters, so the two differ only in those. The
  sleep pacing is deliberately about twice as slow (`PEEMO84_SLEEP_CHAR_MS`
  210 vs 110, longer hold and blank): at the AI's brisk rate the same
  characters read as busy rather than drowsy. The uneven run of dots lives
  in the string itself rather than in variable timing, so pauses of
  different lengths fall out for free and this stays on one code path.
  `SLEEPING` (the 10-minute idle deep sleep) puts it on the AI tab's last
  row exactly as THINKING does, with the log still scrolling above and the
  whole frame already dimmed by the `DimmingDisplay`. `SLEEPY` (the
  clock-driven bedtime nudge) instead swaps the content area for a
  `SYSTEM NOTICE` panel and puts the prompt on its bottom row; the phrase
  above it is `Personality`'s own PT-BR `BEDTIME_MESSAGES` pick, which
  already reaches the log via `pushLogLine`, so nothing extra was plumbed
  for it. That panel used to carry a third chrome line, `RECOMMENDATION:`,
  which was dropped to free the row the prompt now occupies — it was
  spending one of five scarce content rows restating what the phrase
  directly below it already said.
  **COFFEE** keeps the header/status/tabs and yields only the content area
  to the cup — the same "skip that region while this expression is up, no
  state to track" move MATRIX already makes with its log.
  Chrome is English, content is Portuguese, deliberately: `NOW PLAYING` and
  `SYSTEM MONITOR` are the machine's own labels, while everything Peemo
  actually *says* stays in the language it already speaks.
  **The boot sequence** (`drawPeemo84Boot`) is the theme's other bounded
  animation — `PEEMO-84 BIOS`, four POST lines appearing one at a time,
  `SYSTEM READY`, then a centered banner, ~4s in total, after which the eyes
  strike like an old lamp (`peemo84LampLevel`: dark, two failed strikes that
  flare and die, then a climb to full — a breakpoint table rather than a
  curve, because the misfires *are* the effect and no easing function
  expresses "it nearly caught, then dropped"). Being bounded, it needs a
  time anchor the way P2M2's three-flash FAILED does, hence
  `FaceState::themeStartedMs`. It plays on **every** `THEME PEEMO84` command
  rather than once per power-on, and that is the mechanism, not a
  side effect: Core has no "a PC app just connected" signal, but
  `Brobot.Sender` sends `THEME` first on a fresh link and re-sends it on
  every reconnect, so the command *is* that signal — the only way "boot when
  Peemo reaches the PC" was expressible at this layer.
  **Sounds are unchanged** — PEEMO84 plays the same R2D2-flavored cues as every
  other theme. That's an explicit decision to leave `Buzzer` alone (it takes
  no `Theme` today), not an oversight; giving PEEMO84 its own square-wave beeps
  would be a `Theme` parameter on `playForExpression` plus one more
  `SoundSegment` table, nothing structural.
  **Game Mode in the themes with no log** (`drawStatsMessage`): CLASSIC and
  P2M2 have no console log to put stats in (MATRIX and PEEMO84 both do, and
  each renders them in its own MONITOR tab — as text rows and as bar meters
  respectively), so while PLAYING is on screen
  the message box grows (`drawMessageBox` takes a line count for this) and
  becomes the readout — one reading per row, matching how the MONITOR tab
  lists them, with the game's name wrapped above.
  The two themes get different heights for a physical reason, not a stylistic
  one. CLASSIC takes 5 rows because it can move its eyes out of the way:
  `GAME_EYE_SIZE`/`GAME_EYE_Y` shrink them to 26px and pin them at y=18, just
  under the weather/clock strip — the same "this expression reorganizes the
  frame" move COFFEE already makes for its cup, and only while PLAYING is
  rendering. P2M2 gets 4 rows and keeps its plate untouched, because its
  lens is a fixed disc ending at y=77 and a 5-row box would start at y=71 and
  cover the bottom of R2's eye; 4 rows start at 80 and clear it. That's the
  most this theme can grow without redrawing the plate, which is why a long
  game name still truncates there and doesn't in CLASSIC.
  The three stat rows are anchored to the bottom of whichever box, so the
  numbers hold still while the name above them wraps to different heights.
  Drawn directly rather than pushed
  through the message system on purpose: a message there types in one
  character at a time and then expires, which is right for something said
  once and wrong for a panel replaced every two seconds — it would retype
  itself on every `STATS`, and the numbers would never sit still long enough
  to read. Bypassing it is also what keeps the panel open with no expiry to
  fight. The game's name still arrives as the background tier's own message,
  so nothing extra was plumbed through for it; it's simply drawn rather than
  typed, which does mean it shows the typewriter's partial text for the first
  second after a game starts.
  **AI session telemetry in the log themes** (`drawAiStatsRows`, `AISTATS`):
  what the MONITOR tab's stat rows are to a game, these two rows are to a
  Claude Code session — `Opus CTX 42%` over `$1.24 5H 31% 7D 12%`, drawn in
  the **AI** tab of `MATRIX` and `PEEMO84` only. Two rows and not five because
  the AI tab has no slack: every row spent here is a log line lost, so the
  five figures are packed rather than given a row each (worst case is 22
  chars = 132px at `CHAR_ADVANCE_PX`, which is what holds
  `AI_MODEL_NAME_CAPACITY` to 13 characters). They're pinned to the *bottom*
  of the region with the log scrolling above them — the opposite anchoring to
  MONITOR's, where the stats follow the game's name down the screen — for the
  same reason `drawStatsMessage` bottom-anchors its own numbers: a readout
  replaced on every assistant message has to hold still to be readable.
  Both themes call the same function, which is why it takes position and
  color as parameters, exactly the treatment `drawMatrixLog` already got.
  `-1` prints as `--` on the identical reasoning as the machine stats: a
  session that has made no API call yet genuinely has no percentage, and
  rate limits are absent altogether on some plans. A missing *model name* is
  the one field that just drops its column instead of showing a placeholder —
  unlike a percentage, it isn't a reading anyone is waiting on.
  `CLASSIC`/`P2M2` have no log to put this in and deliberately get nothing:
  there the same information keeps arriving as the ordinary `ContextUsage`
  `MSG` those themes already showed.
