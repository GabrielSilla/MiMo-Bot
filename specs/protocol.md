# Protocol (see PROTOCOL.md for the full spec)

Text lines over Serial (COM), or over a plain TCP socket — either the
native/TCP dev build, or a real ESP32's own WiFi TCP server (`WifiSetup.cpp`
+ `PROTOCOL_TCP_PORT` in `Config.h`, port 5555) — same line protocol either
way. Control commands flow PC→Core, draw commands flow Core→PC (only when
`vscreen=1`, or always in the native build):

```
FACE <NEUTRAL|HAPPY|SAD|ANGRY|SLEEPING|SLEEPY|COFFEE|MUSIC|WATCHING|ERROR|READING|FINISHED|THINKING|PLAYING|BYE|IDLE|IDLE_GAME|IDLE_MEDIA>
MSG <text>                    (empty text clears the message)
WEATHER <tempC> <condition>   (CLEAR|CLOUDY|RAIN|STORM|SNOW|FOG; empty clears the badge)
TIME <HH:MM>                  (empty clears the clock)
NOTIFY <FACE> <text>          (top-priority full-screen interruption, auto-clears after 10s)
REPORT <buildOk> <buildFail> <commits> <meetingMin> <mediaMin> <videoMin> <gameMin> <RATING> <text>
                               (Relatório do dia — same tier/atomicity as NOTIFY, but Core
                               draws the 7 numbers as 8 stacked lines with small top-pinned
                               eyes instead of word-wrapping one string; RATING is one of
                               PESSIMO|RUIM|QUESTIONAVEL|MEDIO|BOM|EXCELENTE; commits comes
                               from hooks/mimo-git-hook.ps1's post-commit hook, not a
                               Windows monitor like the other fields; videoMin is a subset
                               of mediaMin — YouTube specifically, tab focused, see
                               YouTubeTabDetector.cs — not a separate activity)
THEME <DEFAULT|MATRIX|MI2MO2|MI84> (persistent, like WEATHER/TIME — see Face.cpp's theme notes)
CLASSICCOLOR <BLUE|GREEN|AMBER|RED|PINK|WHITE> (DEFAULT theme's own primary
                               color — eyes, corner icons, weather/clock
                               badge. No effect on any other theme, which
                               has its own fixed palette. Persistent like
                               THEME, and kept even while a different theme
                               is selected, see Face.cpp's ClassicColor)
SOUND <ON|OFF>                (persistent; buzzer cues, see Buzzer.cpp)
SCANLINES <ON|OFF>            (persistent; physical display's CRT post-FX only)
STATS <cpu%> <cpuTempC> <gpu%> <gpuTempC> <ram%>   (-1 = no source; empty clears)
AISTATS <ctx%> <costCents> <rate5h%> <rate7d%> <model>  (-1 = no source; empty clears)
PONG START|STOP|<KEY LEFT|RIGHT DOWN|UP>   (see firmware-platform.md's PongGame.cpp entry)
RPG START|STOP|LEFT|RIGHT|CONFIRM          (see firmware-platform.md's RpgBattle.cpp entry)
PING                          (Core replies "MIMO 1" — the only command it answers)

CLR r g b
PIXEL x y r g b
RECT/FILLRECT x y w h r g b
RRECT x y w h radius r g b
TEXT x y r g b <LATIN|AUREBESH> text...
PRESENT
```

`PING` is the one command that flows *back* Core→PC, and the only one that
touches neither `Personality` nor `DeviceSettings` — it's about the link, not
about Brobot. It exists so a PC app can find MiMo on the network after DHCP
moves it; see `MimoDiscovery` under [connection.md](connection.md).
`PONG OVER <score>` and `RPG OVER <VICTORY|DEFEAT|FLED>` are the only other
Core→PC lines, and only ever follow a `PONG`/`RPG` command — written
straight to the `Stream` the same way `PING`'s reply is, right before a
`PRESENT` so they flush through the PC side's existing frame-batching (see
PROTOCOL.md's Pong/Batalha RPG sections for the full mechanism).

`PONG`/`RPG` are a different kind of command from everything else above:
each puts Core into an **exclusive** mode (`PongGame`/`RpgBattle`, see
[firmware-platform.md](firmware-platform.md)) that bypasses `Personality`/`Face` entirely for
as long as it's active, rather than adding a fifth priority tier — the same
bypass trick `main.cpp` already used for the WiFi setup portal screen.
`Protocol::dispatch` refuses to start either while the other is already
active, so exactly one of Personality/PongGame/RpgBattle ever owns the
frame at a time.

`FACE`/`MSG` are arbitrated by **four** independent priority tiers on Core,
not by whichever PC app last happened to send one — see Personality.cpp
below. Highest first:

```
NOTIFICATION  >  FOREGROUND (AI)  >  GAME  >  MEDIA
```

**Notifications** (`NOTIFY`) are the things MiMo interrupts you *for* —
Pausa's break reminders, Clima's weather-change alerts, the bedtime nudges.
They outrank everything including AI activity, take the entire frame (no
badges, no log, no message box — see `FaceState::isNotification` and
`drawNotificationScreen`), and expire on their own after
`NOTIFICATION_DURATION_MS` (10s — raised from 7 because the message types
itself in inside that same window, so a long phrase spent most of a 7s one
still appearing, and because both dedicated animations want room to play
more than once).

MiMo's **face stays visible in every notification**. An earlier version gave
the whole frame to the artwork, so a notification with no illustration of
its own fell back to an empty framed card — unreadable, because it wasn't
depicting anything. Making the eyes the constant and the artwork the
optional extra removed the need for a placeholder at all. `COFFEE` puts
small eyes left and slightly *above* the cup's resting line, with the
steaming mug to the right; every ~2.8s that mug rises and drifts toward the
face, holds, and settles back onto the saucer — MiMo taking a sip. The
gesture is pure translation: there is no rotation at this resolution, and a
tilted mug built from fillRects reads as a broken one rather than a tipped
one. (Its handle was also wrong until now — the inner cut's right edge
landed exactly on the outer block's, erasing the whole right wall, so the
"handle" was two prongs with nothing joining them. A real bug, fixed once.) `WEATHER` puts him beside an umbrella with rain
falling behind both — drawn before the canopy and the eyes, so drops that
would land on the umbrella are simply painted over, which reads as shelter
with no per-drop collision test. Its wire token is just `WEATHER`: the
artwork is chosen from `FaceState::weatherCondition`, which the `WEATHER`
command already keeps current for the badge, so the alert and the badge
cannot disagree and each further condition costs one `case` rather than a
command and an enumerator of its own (RAIN gets the umbrella; STORM adds a bolt
and a full-frame flash done by *swapping* the palette's ink and background
for a few frames, which silhouettes the whole scene at once instead of hiding
it under a bright rectangle; FOG drifts horizontal bands in two passes, ink
ones behind him and thinner background ones in front so he is eaten into and
comes back; CLOUDY drifts two flat-bottomed clouds above him — deliberately
not a cloud crossing the sun, since both are ink and would fuse rather than
occlude; SNOW has none, by decision. CLEAR gets a
sun turning in the top-right corner — a stepped disc plus 8 three-block
rays placed by angle, one turn every 6s, using only `sin` with the cosine
taken as `sin(t + pi/2)` since the native build's `Arduino.h` shim never
wires up `<math.h>`; the rest show him alone and centred, since an umbrella
beside a "clear skies" alert would contradict it). This is also why `Brobot.Sender` sends `WEATHER`
*before* the `NOTIFY WEATHER` — the other order would illustrate the alert
with the previous condition. `SLEEPY` makes the eyes *be* the
animation — a 3.6s cycle of lids sagging shut on a squared curve, a beat
held nearly closed, a startled snap open past normal size, then three quick
blinks — anchored on `FaceState::notificationStartedMs` rather than raw
`nowMs` so it always begins awake; everything else just blinks normally.
Every notification drawing takes the **background color as a parameter**
rather than assuming black: the "cut a gap" trick this codebase uses
everywhere fills the cut with the background, and MI2MO2's ground is R2's
light plate, so assuming black gave the mug two black holes and each eye
four dark specks at its corners. Two real bugs, fixed once — `drawEye`/
`fillRoundedRect` now default those parameters to black so no existing
caller changed. Nothing is restored afterwards because
nothing was displaced: no lower tier is touched to make room, so the frame
after expiry just draws what was always underneath. They arrive as one
atomic line rather than the usual `FACE`+`MSG` pair precisely because they
are the top priority — a two-command form could be caught half-applied, and
a lone `MSG` would be routed by `_lastCommandTier` instead. Core raises the
bedtime one itself, with no PC app involved (`raiseNotification`, which is
why that is a separate function from `onNotifyCommand`).

**GAME and MEDIA** used to be a single shared BACKGROUND tier where
whichever arrived last won, so starting music during a game replaced
"Jogando X" with the track and never brought it back. They rank against each
other now — a game is what you're actually doing, music is what's on behind
it — which is what forced `FACE IDLE` to grow the `IDLE_GAME`/`IDLE_MEDIA`
variants: a single "clear the background" no longer says which of the two it
means. Plain `IDLE` still clears both, which is what a client predating the
split intends by it.
**Foreground** (`THINKING`/`READING`/`FINISHED`/`HAPPY`/`SAD`/`ANGRY`/
`SLEEPING`/`SLEEPY`/`COFFEE`/`ERROR`/`NEUTRAL`) always wins the render while
active — most of these are driven by Atividade da IA, but `SLEEPY` is
autonomous (see [firmware-personality.md](firmware-personality.md)) and `COFFEE` is driven by Pausa's own two daily
reminder times, not the AI. **Background** (`MUSIC`/`WATCHING`/`PLAYING`, driven by
Mídia/Jogos) holds underneath it and reappears automatically — face and
message both, with no resend needed — the moment foreground releases the
screen. `FACE NEUTRAL` clears only foreground; `FACE IDLE` clears the
game and media tiers (`FACE IDLE_GAME`/`FACE IDLE_MEDIA` clear one each) — sending the wrong one for the intent leaves the other tier
stuck, so Mídia/Jogos must send `IDLE` (never `NEUTRAL`) to release their own
sticky expression.

`WEATHER`/`TIME` are persistent top-corner overlays, independent of
`FACE`/`MSG` — they don't expire, interrupt, or get interrupted by them
(see Face.cpp below). Core has no RTC or network of its own, so both are
only ever as fresh as whatever PC app last pushed them.

**Resolution is 160x128 (landscape)** and must stay identical in
`SimulatorDisplay.LogicalWidth/Height` (C#) and `Config.h`'s
`LOGICAL_WIDTH/HEIGHT` (C++) — nothing enforces this automatically.
