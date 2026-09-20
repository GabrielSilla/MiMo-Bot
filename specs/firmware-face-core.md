# Firmware Internals — Face.cpp (core rendering)

- **`Face.cpp`**: eyes are two filled rounded-squares (42px, teal by default —
  the wire's `CLASSICCOLOR` command, see PROTOCOL.md, picks a different one of
  six for `THEME DEFAULT` only; every other theme keeps its own fixed palette
  and ignores it. `classicColorRGB` resolves `FaceState::classicColor` to
  RGB once per frame and feeds the same variable into the eyes, the corner
  icons, the weather/clock badge, and a `DEFAULT` notification's ink — the
  places `EYE_R/G/B` used to be hardcoded directly. GREEN/AMBER deliberately
  resolve to `MATRIX_R/G/B`/`MI84_INK_R/G/B` rather than getting new values
  of their own, so picking "green" here reads as the same green MiMo already
  shows elsewhere), corners faked
  with a 2-row "staircase" background cut (not a true circle — keeps serial
  bandwidth down). Eye size/position stay constant whether or not a message is
  showing. Most expressions (HAPPY/SAD/ANGRY/SLEEPING/SLEEPY) just tweak eye height/gap/offset,
  but a few replace the eye shape outright using the same "compose it from small
  `fillRect` blocks" trick: **HAPPY** and **FINISHED** both draw a "^" caret
  per eye (`drawEyeCaret`) — HAPPY used to be a plain rectangle at 55% height,
  which put it on the same "how shut is the eye" scale as SLEEPY (0.75) and
  SLEEPING (0.20) and so read as drowsy rather than as an emotion; a caret's
  apex sits at the top with the strokes falling away, which is what a smiling
  eye does when the cheek pushes the lower lid up. The two share the shape and
  are told apart by motion: HAPPY bounces gently, FINISHED holds still.
  **BYE** is the one expression where MiMo has a hand, waving by real
  rotation about the wrist (see PROTOCOL.md). **FAILED** draws an X (`drawEyeX` — step half the block
  size, or the two diagonal strokes leave a gap exactly where they cross in the
  middle), **FINISHED** draws a "^" caret per eye (`drawEyeCaret`, self-centered in
  the eye box, since a squint alone reads as closed/sleepy rather than happy), and
  **READING** keeps normal eyes but sweeps them with a deliberately asymmetric
  timing (`readingSweep`: slow left-to-right, fast snap back — like scanning a line
  then jumping to the next one) plus a small bobbing book icon in the corner, and
  **THINKING** slices each eye into horizontal bands (`drawEyeGlitch`) and shifts
  each band left/right by a small pseudo-random offset, re-rolled every
  `GLITCH_INTERVAL_MS` (~120ms) — a deliberately jarring "signal interference"
  look, unlike the eased smoothstep motion everything else uses. `Face::render`
  has no timers/state of its own (see Face.h), so the offsets come from a small
  hash of `(nowMs / GLITCH_INTERVAL_MS, band, eyeIndex)` rather than an evolving
  `random()` seed, which is also why the two eyes glitch independently of each
  other instead of moving in lockstep. Message text word-wraps into a **fixed
  3-line window** (`MESSAGE_VISIBLE_LINES`),
  bottom-anchored; once a 4th line would be needed the oldest visible line scrolls
  off, like a terminal. Sleeping shows a bobbing "Z Z Z" near the right eye.
  THINKING has no corner icon — the glitch eyes plus the "Pensando..." message
  carry the expression on their own (in `THEME MATRIX`, see `drawMatrixRain`
  below, THINKING additionally swaps the console log for a rising-character
  "digital rain" animation in that same region — messages come back the
  moment THINKING clears). **PLAYING** (Sender's Jogos card) keeps
  eyes fully open like MUSIC/WATCHING/READING — no shape change — and a small
  bobbing gamepad icon (`drawGamepadIcon`) carries the expression instead: one
  solid body block plus a d-pad cross and two face buttons cut out of it in
  background color, the same "cut a gap from a filled block" trick the eye
  corners and the book's spine gap already use, rather than drawing new shapes
  on top. The MUSIC/WATCHING/READING/PLAYING corner icons (note/play/book/gamepad) sit at
  `CORNER_ICON_Y_SHIFT` down from the top-left — pushed down out of the fixed
  strip the WEATHER/TIME badges occupy (see below); their x-range never overlaps
  the eyes regardless of expression, so the vertical push is the only constraint.
  **COFFEE** (Sender's Pausa card) is the one expression that changes the eyes'
  size/position instead of just their shape: `Face::render` swaps in
  `COFFEE_EYE_SIZE`/`COFFEE_EYE_GAP` (smaller) and pins them to
  `COFFEE_EYES_X` near the left edge rather than centering them, clearing
  the whole right side of the frame for `drawCoffeeCup` — a saucer + hollowed-
  out mug + handle stub (same cut-a-gap trick as everything else), with three
  steam wisps that rise and sway on a continuous `nowMs % COFFEE_STEAM_CYCLE_MS`
  loop rather than a bounded one-shot animation, since they need to keep
  going for as long as the reminder message stays up. `Personality`'s
  hold-still list includes COFFEE for a concrete reason, not just
  consistency: with the eyes pinned to a near-fixed spot, an active
  look-around offset could otherwise push them past the left edge.
  `drawWeatherBadge`/`drawClockBadge` draw those two badges independent of
  `state.expression` entirely — small procedural pictograms (sun/cloud/rain/
  storm/snow/fog), same block-composition style as everything else, deliberately
  not PNG/bitmap (see PROTOCOL.md's `DrawBitmap` note — at this resolution a
  downscaled bitmap would look worse, not better, and needs a whole asset/wire
  pipeline this doesn't).
- **`drawReportNotification`** (Brobot.Sender's Relatório do dia, `REPORT`
  in PROTOCOL.md) is the one notification whose eyes shrink **up** instead
  of **left**: every other notification with its own artwork (`COFFEE`/
  `EMAIL`/`MEETING`/`ACHIEVEMENT`) frees the right side of the frame for an
  icon, but this one needs the *entire* frame below the eyes for eight
  stacked stat lines instead of one wrapped sentence — a real usability
  complaint on the physical display, everything running together in prose
  instead of being scannable per item. `NOTIF_REPORT_EYE_SIZE`/`_GAP`/`_Y`
  pin small (18px) eyes to top-center via the same generic
  `drawNotificationEyes(centerX, topY, ...)` every other notification icon
  already calls — no changes needed there, it already took position as
  plain parameters. Eight `display.drawText` calls follow at
  `MESSAGE_LINE_HEIGHT` (9px) pitch, left-aligned at `NOTIF_REPORT_STATS_X`
  (tight, hand-checked gaps — 8 stat lines plus the message's own 3 below
  them leaves only ~3px of the 128px frame to spare, see
  `NOTIF_REPORT_STATS_TOP_Y`/`_MESSAGE_TOP_Y`'s own comments): build
  success/fail counts, git commit count (from `hooks/mimo-git-hook.ps1`'s
  `post-commit` hook, not a Windows monitor), meeting/media/video/game time
  (`formatReportMinutes`, `"Xh20"`/`"Nmin"` — video is a subset of media,
  specifically a focused YouTube tab, see `YouTubeTabDetector.cs` on the PC
  side), and the rating (`dailyRatingLabel`, no accents — same convention
  as `BEDTIME_MESSAGES` in Personality.cpp). None of these eight type in —
  they're numbers Sender already computed, not speech, same
  reasoning the coffee cup or trophy badge never animate character-by-
  character either. `drawNotificationText` (the shared word-wrapped message
  renderer every notification's trailing casual text goes through) gained a
  `topY` parameter specifically for this: REPORT's own casual phrase still
  types in and word-wraps exactly like any other notification's message,
  just starting at `NOTIF_REPORT_MESSAGE_TOP_Y` (below the eight stat lines)
  instead of the fixed `NOTIFICATION_TEXT_TOP_Y` every other notification
  uses.
