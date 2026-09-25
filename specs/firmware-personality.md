# Firmware Internals — Personality.cpp

- **`Personality.cpp`**: blink and look-around use eased (smoothstep) transitions
  spread over enough frames to look smooth at ~20fps, not instant jumps. Look-around
  picks randomly from 8 directions (incl. diagonals) and swings close to the
  screen edges — except while `THEME MATRIX` or `THEME PEEMO84` is active
  (both pin the eyes to the bottom), where the 3 downward
  ones (down, down-left, down-right; `LOOK_DIRECTIONS_NO_DOWN`, indices into
  the same `LOOK_DIRECTIONS` table so both pools stay in sync with
  `LOOK_OFFSET_X_PX`/`Y_PX`) are excluded from the pool — both pin the
  eyes near the bottom edge (see `MATRIX_EYE_BOTTOM_MARGIN` in `Face.cpp`,
  which PEEMO84 reuses rather than defining its own), leaving too little
  clearance to look further down without crowding or crossing that edge. `THEME P2M2` filters the same pool for a different
  reason (`LOOK_DIRECTIONS_NO_UP_LEFT`): it doesn't move an eye at all, it
  slides a reflection across a fixed lens (see `Theme::P2M2` above), and
  up-left is the single direction whose offset carries that glint off the
  edge of the glass — up alone and left alone both stay inside it, so only
  the one diagonal is dropped rather than a whole side. Falls asleep after `SLEEP_TIMEOUT_MS` (10 min) idle; blink/look
  pause entirely while asleep (also while FAILED/READING/THINKING hold still, or
  MUSIC dances — those drive their own motion off `nowMs` in Face.cpp instead;
  PLAYING is *not* in that hold-still list, same as WATCHING — the gamepad icon
  bobs on its own, but the eyes keep blinking/looking around normally, since
  "playing a game" isn't a motionless state the way sleeping or reading is).
  `_expression`/`_gameExpression`/`_mediaExpression`, plus the notification
  tier above them all, are independent priority tiers
  (`Personality::Tier`), each with its own `TypedMessage` (typewriter state
  + expiry), not one shared expression/message pair — this is what lets an
  AI message interrupt MUSIC/WATCHING/PLAYING without losing track of it: a
  real bug, fixed once (previously all four of MUSIC/WATCHING/THINKING/
  PLAYING lived in the same sticky `_expression`, so whichever the AI's
  THINKING/READING/FINISHED last overwrote never came back once its own
  10s-ish window expired, even though the media/game was still going).
  `MUSIC`/`WATCHING` live in `_mediaExpression` and `PLAYING` in
  `_gameExpression` (both sticky by construction — nothing times either out,
  see `resolveExpression`), set/cleared by `FACE MUSIC|WATCHING`/`FACE
  IDLE_MEDIA` and `FACE PLAYING`/`FACE IDLE_GAME`, never `_expression`. The
  two were one shared slot until the notification work split them; a game
  now outranks media rather than whichever arrived last winning. `THINKING` is the one remaining sticky *foreground*
  expression (`_expression == THINKING` short-circuits
  `resolveExpression`); every other foreground expression times out via
  `_expressionOverrideUntil` (`FACE_OVERRIDE_DURATION_MS`, 4s) same as
  before, and `FACE NEUTRAL` collapses that window to `now` (not `+4s`) so a
  stored background can take over immediately instead of sitting through a
  redundant "showing NEUTRAL" wait. Once foreground's override lapses,
  `resolveExpression` falls back to `_gameExpression`, then
  `_mediaExpression` (whichever is set) before
  falling further to idle/SLEEPING/NEUTRAL — that fallback, plus each tier
  keeping its own message text, is what makes MUSIC/WATCHING/PLAYING (and
  their "now playing"/"Jogando X" label) reappear on their own once an
  interrupting AI message goes away, instead of the two racing to overwrite
  a single shared expression/message pair.
  A background `FACE` command received while foreground is active only
  updates the stored background state (`onFaceCommand`) — it doesn't touch
  what's currently rendering, so starting music/a game mid-AI-message keeps
  it hidden until the AI message clears, per the same fallback. `MSG` with no
  preceding `FACE` in the same "turn" (a `Notification` hook event, which
  intentionally sends no `FACE` — see `OnAiThoughtReceived`) routes to
  whichever tier the *previous* `FACE` command belonged to (`_lastCommandTier`)
  — and `onMessageCommand` also stretches `_expressionOverrideUntil` far
  enough for that message's own typing+hold time, so a bare Notification (no
  `FACE` at all) still counts as high-priority foreground content that
  interrupts background instead of being silently absorbed by it.
  Each tier's message is sticky the same way it always was — MUSIC/
  WATCHING/PLAYING's `TypedMessage` never auto-expires (`persistDurationMs
  == 0`) since it's a "now playing"-style label, and THINKING's foreground
  message doesn't either while `_expression == THINKING`; every other
  foreground message still auto-clears `MESSAGE_DURATION_MS` (10s) after
  typing finishes. Messages "type" in character-by-character
  (`TYPING_CHAR_INTERVAL_MS`) regardless of which tier is currently
  rendered — a background message keeps typing in while hidden behind an
  active foreground one, so it's already sitting there fully revealed the
  instant it becomes visible again, no retyping. `MESSAGE_CAPACITY` is 255
  chars, per tier. `TypedMessage::set` trims anything longer to fit and
  appends `"..."` inside that same capacity rather than just dropping the
  tail — a message that's cut mid-word/mid-sentence with no signal it was
  cut reads as Peemo saying something nonsensical, which is exactly what a
  verbose `last_assistant_message` from Stop used to produce. This was a
  real bug, fixed once.
  `onThemeCommand` stamps `_themeChangedAt` on **every** `THEME` command, not
  only on an actual change of value, and that's what PEEMO84's boot sequence
  hangs off (`FaceState::themeStartedMs`). Core has no "a PC app just
  connected" signal of its own, but `Brobot.Sender` sends `THEME` as the
  first thing over a fresh link and re-sends it on every reconnect (see
  `UpdateConnectionStatus`), so the command *is* that signal — which is the
  only reason "play the boot sequence when Peemo reaches the PC" is
  expressible down here at all. The other themes never read the field.
  `onWeatherCommand`/`onTimeCommand` deliberately never touch `_lastInteractionAt` —
  they're passive background telemetry from whichever PC app is connected, not user
  interaction, so a Sender pushing `TIME` every minute must not keep Brobot awake
  forever. A short **boot animation** plays once per `begin()` (i.e. once per
  power-on/reset, anchored by `_bootStartedAt`): the eyes drop in from just
  above their resting position with an overshoot/bounce (`easeOutBack`, ~700ms),
  then settle with a quick decaying side-to-side wobble (~500ms) — "falling and
  reorganizing" rather than popping straight into view. Deliberately no
  `sinf`/`powf`/libm calls (`easeOutBack`/`triangleWave` are hand-written
  polynomial/piecewise functions) since this file also has to compile against
  `BrobotCore/native`'s minimal `Arduino.h` shim, which doesn't wire up
  `<math.h>` — same reasoning as `smoothstep` above.
  **`SLEEPY`** is a second, lighter sleep state, entirely clock-driven
  (`isBedtimeHour`, 22h-6h) rather than idle-driven like `SLEEPING` — it wins
  over plain `NEUTRAL` in `resolveExpression`'s fallback, but the 10-minute
  idle `SLEEPING` check still takes priority over it (once actually asleep,
  the drowsy nudge stops making sense). Visually it's `NEUTRAL`'s eye shape
  just slightly squinted (`shapeFor`, no new Face.cpp render branch needed —
  it falls through to the plain `drawEye` path, no icon, no "Z Z Z"), but the
  blink itself runs on a much longer `BLINK_DURATION_SLEEPY_MS` (900ms vs the
  normal 280ms) so it reads as an obviously heavy-lidded blink rather than
  the quick NEUTRAL one — `updateBlink` picks the duration off
  `_renderExpression` each call. Unlike `SLEEPING`/`FAILED`/`READING`/
  `THINKING`/`MUSIC`, `SLEEPY` is *not* in `update()`'s hold-still list, so
  ordinary blink/look-around keep running (just with that slower blink).
  While it's bedtime hours, `Personality` also autonomously picks one of 10
  hardcoded PT-BR "go to sleep" phrases (`BEDTIME_MESSAGES`) into its own
  `_bedtimeMessage` (a third `TypedMessage`, alongside foreground/
  background — not either of those tiers since no PC app is commanding this)
  the moment bedtime starts, then again every `BEDTIME_MESSAGE_INTERVAL_MS`
  (30 min) for as long as it stays bedtime, tracked independently of
  whatever's actually rendering so the schedule doesn't drift if AI activity
  or media is occupying the screen when a 30-minute mark passes. Each pick
  auto-clears `MESSAGE_DURATION_MS` (10s) after typing finishes, same as a
  normal foreground message, instead of sitting on screen for the full 30
  minutes until the next one replaces it.
  `currentState()` only shows `_bedtimeMessage` while `_renderExpression ==
  SLEEPY`. All of this is driven off `_timeText` — the same field `TIME`
  already populates for the weather badge's day/night icon (see
  `isNightHour` in `Face.cpp`) — since Core still has no RTC of its own;
  `FACE SLEEPY` is also accepted as an explicit command (`parseExpression`)
  for testing without waiting on the clock.
