# Firmware Internals — Face.cpp (core rendering)

- **`Face.cpp`**: eyes are two filled rounded-squares (42px, teal by default —
  the wire's `CLASSICCOLOR` command, see PROTOCOL.md, picks a different one of
  six for `THEME DEFAULT` only; every other theme keeps its own fixed palette
  and ignores it. `classicColorRGB` resolves `FaceState::classicColor` to
  RGB once per frame and feeds the same variable into the eyes, the corner
  icons, the weather/clock badge, and a `DEFAULT` notification's ink — the
  places `EYE_R/G/B` used to be hardcoded directly. GREEN/AMBER deliberately
  resolve to `MATRIX_R/G/B`/`PEEMO84_INK_R/G/B` rather than getting new values
  of their own, so picking "green" here reads as the same green Peemo already
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
  **BYE** is the one expression where Peemo has a hand, waving by real
  rotation about the wrist (see PROTOCOL.md). **FAILED** draws an X (`drawEyeX` — step half the block
  size, or the two diagonal strokes leave a gap exactly where they cross in the
  middle), **FINISHED** draws a "^" caret per eye (`drawEyeCaret`, self-centered in
  the eye box, since a squint alone reads as closed/sleepy rather than happy), and
  **SWEATING** (`drawEyeWorried`) slants each eye's top edge — high at the
  inner corner, dropping `WORRIED_SLANT_FACTOR` (24%) of the eye's height
  toward the outer side — by cutting background-colored rows off the
  finished rounded square, and adds `drawSweatDrop`: a 5x9 teardrop beside
  the right eye's outer corner that slides down with a `k²` gravity ease
  over 1.5s, hides for 0.4s, and loops off free-running `nowMs`. White in
  CLASSIC (so it never merges with a blue/teal CLASSICCOLOR), the theme ink
  elsewhere, skipped in P2M2. It also has its own notification screen
  (`drawSweatingNotification`) — unlike ANGRY/SAD, whose shape tweaks the
  generic notification fallback ignores. Added for Brobot.Sender's Alertas
  de desempenho (`NOTIFY SWEATING`). **READING** keeps normal eyes but sweeps them with a deliberately asymmetric
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
  icon, but this one needs the frame below the eyes for stacked stat lines
  instead of one wrapped sentence — a real usability complaint on the
  physical display, everything running together in prose instead of being
  scannable per item. Nine items total don't fit one screen readably (a
  previous version tried — cramped 16px eyes, a 2-line message with
  almost no margin) — `REPORT` now spans **two pages**, up to
  `NOTIF_REPORT_PAGE_ITEMS` (6) items each (today: 6 then 3, since nine
  doesn't split evenly), each page getting the exact same roomy layout the
  original 6-line design had (`NOTIF_REPORT_EYE_SIZE` back to 18px).
  `reportOnSecondPage(sinceStart)` compares elapsed time against
  `NOTIF_REPORT_PAGE_DURATION_MS` (5s — shorter than every other
  notification's 10s, product decision: a page is only ever read, never
  typed out character-by-character like the casual phrase is — defined in
  Face.h since `Personality::raiseNotification` needs it too, see below)
  to pick a page; `reportItemsOnPage(secondPage)` says how many stat lines that page
  actually has, which both `drawReportNotification` (to know which slice
  of its 9-item array to draw) and `drawNotificationScreen` (to know where
  the casual phrase should start on *this* page) need to agree on.
  `display.drawText` calls follow at `MESSAGE_LINE_HEIGHT` (9px) pitch,
  left-aligned at `NOTIF_REPORT_STATS_X` (the three build/commit lines are
  omitted when Sender sent -1 for them — its Ferramentas de Dev card is
  off — see `reportHasDevTools`/`reportItemCount`; the remaining 6 items
  fit one page, which then simply stays up for the whole 10s): build success/fail counts, git
  commit count (from `hooks/peemo-git-hook.ps1`'s `post-commit` hook, not a
  Windows monitor), meeting/media/video/social/game time
  (`formatReportMinutes`, `"Xh20"`/`"Nmin"` — video (labeled "Youtube" on
  screen) and "Rede Social" are both subsets of media, specifically a
  focused YouTube tab (`YouTubeTabDetector.cs`) and a focused
  TikTok/Instagram/Facebook tab (`SocialMediaTabDetector.cs`)
  respectively, kept as two separate lines rather than merged into one —
  product decision, not technical), and the rating (`dailyRatingLabel`, no
  accents — same convention as `BEDTIME_MESSAGES` in Personality.cpp).
  None of these type in — they're numbers Sender already computed, not
  speech, same reasoning the coffee cup or trophy badge never animate
  character-by-character either. `drawNotificationText` (the shared
  word-wrapped message renderer every notification's trailing casual text
  goes through) gained `topY` and `lines` parameters specifically for
  this: REPORT's own casual phrase still types in and word-wraps exactly
  like any other notification's message, just starting lower — and, with
  the two-page split freeing up room again, back to the usual
  `NOTIFICATION_TEXT_LINES` (3) visible lines rather than a pinched 2. The
  phrase itself is identical on both pages (Sender only ever picks one per
  report) and, since it finishes typing well inside the first page's own
  5s (these phrases are short, and TYPING_CHAR_INTERVAL_MS is only 40ms/char),
  the second page just shows it already fully revealed — no retyping.
  `Personality::raiseNotification` gives `REPORT` its own total duration
  (`NOTIF_REPORT_TOTAL_DURATION_MS`, 10s — exactly 2x
  `NOTIF_REPORT_PAGE_DURATION_MS`, both in Face.h so Face.cpp and
  Personality.cpp can't drift apart on what "a page" means) instead of
  `NOTIFICATION_DURATION_MS` directly — same 10s total every other
  notification gets, just split across two pages instead of shown as one.
