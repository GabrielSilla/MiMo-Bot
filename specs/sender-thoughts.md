# Brobot.Sender Internals — Pensamentos do Peemo (PLANNED, not implemented yet)

> Status: design agreed with the user, nothing built yet. This file is the
> plan to implement from; once the feature lands, rewrite it as the usual
> "how it works and why" spec and link it from CLAUDE.md,
> specs/overview.md and specs/sender-feature-cards.md.

A new checkbox card in Configurações Gerais, **"Pensamentos do Peemo"**. At
random, unpredictable moments through the day Peemo "thinks out loud" — a
short remark about the user's day (built from the Relatório data), the
moment (time, weekday, weather, games), a line from Peemo's own huge phrase
base, a translated random fact, a "neste dia" historical fact, or a heads-up
that the ISS (or another famous satellite) is about to cross the user's sky.
The goal is
to make Peemo feel like a living little creature, not a robot that only reacts.

Consistent with **the one rule** (specs/overview.md): Sender picks *which
text* and *when*, exactly like `GreetingMessages`/`PausaMessages`/
`WeatherAlerts` already do — Core has no RTC, no report data and no network.
Core still owns how it's shown.

## Behavior

- **Toggle**: checkbox persisted in `SenderSettings` (`PensamentosEnabled`).
  The card shows `Último pensamento: <texto>`.
- **Display**: plain `FACE <expr>` + `MSG <texto>` — a normal foreground
  message, **not** `NOTIFY` (a musing must not take over the whole screen).
  Core auto-clears it `MESSAGE_DURATION_MS` (10s) after typing finishes and
  whatever background tier was there (music, etc.) comes back on its own. Not
  `FACE THINKING`: it's the one sticky foreground expression and would need an
  explicit clear. **Exception: space thoughts** go out as `NOTIFY SATELLITE`/
  `NOTIFY SPACE` instead (see "Space thoughts" below).
- **Accents allowed** (ã, ç, é...). Verify `Font5x7` and the physical display
  draw every character used; if a glyph is missing, add it to the font rather
  than stripping accents.
- **Length**: target ≤ ~75 chars per rendered line so it reads well at 160x128.
- **Runs all day** while connected (no business-hours window).

### Timing — deliberately unpredictable

- Next gap = **30 min + exponential(mean 50 min)** → overall mean ~80 min.
  The 30-min floor is *added*, not clamped — clamping would pile every short
  draw onto exactly 30 min and make it predictable again.
- Cap any single gap at ~4h.
- Simulated 9h day: ~6.5 thoughts on average, usually 4–9. Gap distribution:
  30–45 min 28%, 45–90 min 45%, 90–150 min 20%, 150+ min 7%.
- **No daily cap** — the distribution already limits it.
- **No quick follow-up** ("ah, e outra coisa...") — the 30-min floor always holds.
- Restarting Sender draws a fresh schedule.
- **Holds its turn** (postpones until clear, doesn't skip) when:
  disconnected, meeting active, game running, AI message on screen
  (`_aiThoughtFaceActive`, `_aiMessageHoldUntil`), notification recently
  sent, Pong/RPG running, or **user away from the PC** (~10 min with no
  mouse/keyboard input, via `GetLastInputInfo`) — thinks when the user comes
  back instead of talking to an empty room and wasting a phrase/trigger.

### Tone

Friendly accomplice, sarcastic, **never controlling / never a supervisor**.
E.g. "2h de Facebook hoje hein, não deixa seu chefe saber". The existing
15-min social/YouTube `NOTIFY` nudges stay the alert; thoughts are the
loose joke, at another moment.

**How sarcastic is set by Peemo's mood** — see [mood.md](mood.md):
`ANIMADO` (07–16h) leve, `FIM_DE_DIA` (16–22h) médio, `CANSADO` (22–07h)
ácido. Every joking phrase carries a level and is only used in its mood;
neutral content (facts, "neste dia", passes, curiosities) is used in any
mood. mood.md also lists what never goes in, even at ácido.

### Space thoughts

**Anything space-themed, from any source, uses one of the two space
animations** already built in Core (see PROTOCOL.md → Notificações):

| Face | When | Screen |
|---|---|---|
| `SATELLITE` | the thought is about a satellite/space station: a real ISS/Tiangong/Hubble pass, a fact or "neste dia" about a satellite, the ISS, Sputnik, a space station | tilted satellite crossing a starry sky, eyes following it |
| `SPACE` | any other space subject: planets, Moon, Sun, stars, galaxies, astronauts, rockets, NASA, comets, telescopes... | same starry sky and eyes, no satellite |

- Sent as `NOTIFY <SATELLITE|SPACE> <texto>` — the one kind of thought that
  takes the whole screen. Worth it: it's rare (a small slice of each
  source) and it's the moment Peemo "receives a transmission".
- Core opens both with "Transmissão Espacial Recebida!!!" and only then
  types the text, so Sender sends **only the message** — phrases must not
  repeat "transmissão"/"recebi" and should read well right after that line.
- `Thought.Face` carries the choice; the one place that sends a thought
  (`MainWindow`) does `NOTIFY` when `Face` is `SATELLITE`/`SPACE`, `FACE` +
  `MSG` otherwise. Hold conditions are the same for both.
- How each source decides:
  - **SpaceMessages**: always `SATELLITE` (every one of its thoughts is a pass).
  - **PeemoBaseMessages / WorkContextMessages**: the phrase's own face column
    in the data file — space-category phrases (curiosidades do espaço,
    teorias da conspiração espaciais, planos de dominação lunar...) are
    written with `SPACE` (or `SATELLITE` when about a satellite/station).
    The validation script rejects `SATELLITE` on a phrase that doesn't
    mention a satellite/station, and flags space words under a non-space
    face for review.
  - **RandomFactsMessages**: keyword match on the **English** text, before
    translating (more reliable than on MyMemory's output): satellite/ISS/
    space station/Sputnik/Hubble → `SATELLITE`; space, planet, Moon, Mars,
    Jupiter, Saturn, Sun, star, galaxy, universe, astronaut, NASA, orbit,
    comet, asteroid, meteor, telescope, rocket, light-year... → `SPACE`.
    Word-boundary match (so "star" doesn't hit "start", "sun" doesn't hit
    "Sunday"), stored with the fact in the on-disk stock.
  - **OnThisDayMessages**: same idea on the PT text: satélite/estação
    espacial/ISS/Sputnik/Hubble → `SATELLITE`; espaço, espacial, Lua,
    lunar, Marte, planeta, astronauta, cosmonauta, NASA, Apollo, órbita,
    foguete, cometa, telescópio, galáxia... → `SPACE`.
- The same keyword lists live in one small helper (`SpaceTopic.Classify`)
  shared by the two API sources; the validation script reuses them for its
  review flags.

## Sources and weights

| Class | Weight | Content |
|---|---|---|
| `WorkContextMessages` | 40% | ~8,200 phrases grouped by situation (see below), with `{min}`/`{site}`/`{temp}`/`{jogo}`/... placeholders |
| `PeemoBaseMessages` | 30% | ~10,000 standalone phrases in ~45 categories |
| `RandomFactsMessages` | 15% | uselessfacts.jsph.pl (EN) translated to PT-BR via MyMemory |
| `OnThisDayMessages` | 15% | Wikimedia feed `wikipedia/pt/onthisday/selected/MM/DD` (native PT-BR) |

| `SpaceMessages` | — (priority) | ISS/Tiangong/Hubble passes over the user's location, CelesTrak TLE + local SGP4 |

`ThoughtDirector` draws a source by weight, never the same source twice in a
row, and if a source's `TryPick` returns `null` ("nothing fresh right now")
it passes the turn to the next source. Peemo is never silent: the local base
is the final fallback.

`SpaceMessages` is the exception to the weighted draw: it's an event, not a
pool, so it's asked **first** at every slot and only answers when a real
pass qualifies (see below); otherwise it returns `null` and the normal
weighted draw happens. It doesn't count for the "never the same source twice
in a row" rule.

### WorkContextMessages — situations

Each phrase belongs to one situation and can only be used when that
situation holds, so pool size is scaled to how often the situation occurs
per year (the most frequent groups must still take years to exhaust).

| Situation | Groups | Per group | Total |
|---|---|---|---|
| Time of day (madrugada, manhã cedo, manhã, almoço, tarde, noite) | 6 | 300 | 1,800 |
| Weekday | 7 | 150 | 1,050 |
| Weather (condition × cold/mild/hot) | ~12 | 80 | ~960 |
| Report triggers (social per site ×2 levels, YouTube, long meetings, failing builds, many commits, first commit, no-meeting day, lots of music...) | ~15 | 100 | 1,500 |
| Current moment (meeting just ended, build passed, back from lunch...) | ~8 | 80 | 640 |
| Games (see below) | ~6 | ~80 | ~500 |
| Special dates (holidays, Natal, month start/end, payday, sexta-feira 13...) | ~20 | 30 | 600 |
| Combinations (sexta + calor, segunda + chuva, madrugada + commit, madrugada + jogo, build quebrado + jogo...) | ~44 | 25 | ~1,100 |
| **Total** | | | **~8,200** |

- **Triggers don't fire immediately** — a met condition only gets priority
  at the next randomly scheduled slot, so timing stays unpredictable and it
  never lands right on top of an existing `NOTIFY` nudge. Each level fires
  once per day; the same trigger phrase won't repeat within a month.
- **Combinations win** over single situations when several hold at once —
  they read like Peemo connecting things in its head, not reading a list.
- Placeholder phrases are validated with the **largest possible values**
  ("TikTok", "180 min", the longest game name seen) so nothing overflows.
- Dev triggers require the Ferramentas de Dev card; social/YouTube triggers
  require Mídia; game situations require Jogos — same gating the Relatório
  already uses.

**Social per site**: `DailyReportTracker`/`DailyReportStore` start tracking
focused seconds **per site** (Facebook/Instagram/TikTok) so thoughts can
name the site. The Relatório keeps showing the grouped total only.

**Games**: `GameMonitor.GameChanged` already provides the game name, and the
report already sums game time. Track time **per game** in the day's progress
too (report still shows the grouped total). Peemo never thinks *during* a game
(Game Mode screen); game remarks come after it closes or later in the day:
just finished playing, long single session (≥ 1h), daily game time ≥ 1h /
≥ 3h, gaming during business hours, several games today, first game of the
day — plus the combinations above.

### PeemoBaseMessages

~10,000 phrases across ~45 categories (robot existential, observations about
humans, animal/space/body/history/food/tech curiosities, invented tiny
memories, random opinions, unanswerable questions, micro-poems, cute world-
domination plans, conspiracy theories of a robot, ...), mixed formats
(question, statement, exclamation, confession), each with a suggested face.
Shuffled per install, never repeats; never the same category twice in a row.
At ~2/day it lasts ~13 years.

### RandomFactsMessages

- Prefetch and keep 5–10 translated facts **on disk**, refilled in the
  background; `TryPick` only reads the stock, never waits on the network.
- Filter **before** translating: EN length ≤ ~70 chars (PT runs 15–20%
  longer) and a blocklist for heavy content (crime, death, kill...) —
  uselessfacts does return grim ones.
- MyMemory: no key, 5k chars/day anonymous; ~1 fact/day uses ~150.
- Translation stays inside this class; extract a `MyMemoryTranslator` only if
  another source ever needs it.

### OnThisDayMessages

Fetch the day's `selected` events once, cache them, use up to ~1/day. Same
length filter; skip grim events.

### SpaceMessages

"Um satélite famoso vai passar no seu céu" — computed locally, no API key
(project rule: no metered/keyed APIs; N2YO was rejected for that reason —
one key embedded in the installer would be shared by every user).

- **Orbits**: CelesTrak GP data, TLE format, one request per satellite
  (`https://celestrak.org/NORAD/elements/gp.php?CATNR=<id>&FORMAT=TLE`).
  Fetched at most **once a day** (CelesTrak asks clients not to poll more
  than every ~2h; TLEs stay accurate enough for a few days) and cached on
  disk, so a failed fetch just reuses yesterday's.
- **Satellites**: a short fixed list of famous ones only — ISS (25544),
  Tiangong (48274), Hubble (20580). Never Starlink or generic satellites:
  something passes overhead every few minutes, which would kill the magic.
- **Propagation**: SGP4 in-process via a NuGet library (candidate:
  `SGP.NET`; confirm it handles pass prediction/look angles before
  committing to it, otherwise `Zeptomoby.OrbitTools`).
- **Location**: the same Windows `Geolocator` lat/lon `WeatherMonitor`
  already gets — today it's a local inside `WeatherMonitor.RunAsync`, so
  extract a small shared location provider (cached lat/lon) rather than
  requiring the Clima card to be on. No location → `null`.
- **Qualifying pass** (all of them):
  - max elevation ≥ ~30° (low passes are hard to see and don't impress);
  - **visible to the naked eye**: sun ≤ −6° at the observer (dusk/night)
    *and* the satellite sunlit (not in Earth's shadow) at some point of the
    pass — otherwise "passing overhead" is technically true but useless;
  - starts within the next **~3h** (the slot is random, so the remark is a
    heads-up with the time), or is happening right now.
- **Text**: time + direction in 8-point PT compass (norte, nordeste...),
  e.g. "Às 19:42 a ISS passa bem visível no céu, olha pra noroeste!" or,
  mid-pass, "A ISS tá passando em cima de você agora, dá um tchau!".
  Phrase templates live in the source (a few dozen variants, `{sat}`,
  `{hora}`, `{dir}` placeholders), same voice guide as the rest.
- **Display**: `NOTIFY SATELLITE <texto>` (see "Space thoughts").
- **Limits**: each pass announced once; at most one space thought per day.
- Runs all day like the other sources, but visible passes are naturally
  dusk/night only — fine, since the scheduler already holds while the user
  is away from the PC.

## Architecture — `src/Brobot.Sender/Thoughts/`

```
Thoughts/
  IThoughtSource.cs        Name, Weight, Thought? TryPick(ThoughtContext)
  Thought.cs               Face, Text, Source, Category (Face SATELLITE/SPACE = send as NOTIFY)
  SpaceTopic.cs            space keyword lists → SATELLITE / SPACE / none
  ThoughtContext.cs        snapshot: now, DailyReportResult, per-site/per-game minutes,
                           weather, current states (meeting just ended, last game...)
  WorkContextMessages.cs
  PeemoBaseMessages.cs
  RandomFactsMessages.cs
  OnThisDayMessages.cs
  SpaceMessages.cs         CelesTrak TLE cache + SGP4 pass prediction
  ThoughtDirector.cs       which source speaks (weights, no repeats, pass-the-turn)
  ThoughtScheduler.cs      when (30 min + exponential, hold conditions, presence)
  ThoughtHistoryStore.cs   persisted shuffle queues, used phrases, cooldowns, today's fired triggers
  Data/
    peemo-base.tsv          embedded resource
    work-context.json      embedded resource, grouped by situation
```

- **Each source owns its rules and its own data file.** Phrases live in
  embedded data files, not C# arrays — fine for `PausaMessages`' dozens,
  unreadable at 18k lines, and the validation script shouldn't parse C#.
- A new source later = one class implementing `IThoughtSource`, registered
  in `ThoughtDirector` with a weight.
- `MainWindow` only wires the checkbox, builds the `ThoughtContext` from
  what it already has (`_dailyReport`, `_lastWeatherReading`, meeting/game/AI
  flags) and sends `FACE` + `MSG` — or `NOTIFY SATELLITE|SPACE` for a space
  thought.

## Content production

Total ~18,200 phrases — the heaviest part of the work, far more than the code.

- **Voice guide first** (user approves before any generation): first person,
  curious little robot creature, accomplice tone, ≤ ~75 chars. Includes a
  section for space phrases (face `SPACE`/`SATELLITE`): they follow the
  "Transmissão Espacial Recebida!!!" line, so they read as the transmission
  itself, never announce it.
- **Validation script**: length, glyphs supported by the font, exact and
  near-duplicate detection, placeholder rendering with max values, and a
  random sample per category for the user to review.
- **Phase 1**: ~1,000 base + ~1,500 context (every situation covered with
  smaller groups) — test the tone on the real Peemo.
- **Phase 2**: complete to full size with the voice guide adjusted from phase 1.

## Implementation order

1. Skeleton: `IThoughtSource`, `ThoughtScheduler`, `ThoughtDirector`,
   `ThoughtHistoryStore`, checkbox + `SenderSettings`, presence check,
   per-site and per-game tracking in `DailyReportTracker`, and the
   `FACE`+`MSG` vs `NOTIFY SATELLITE|SPACE` routing in `MainWindow`. (The
   two Core animations themselves are already done.)
2. API sources: `RandomFactsMessages`, `OnThisDayMessages`, `SpaceMessages`
   (incl. the shared location provider extracted from `WeatherMonitor`),
   plus `SpaceTopic` classifying the first two.
   Modo teste should be able to force a space thought using the next
   visible pass even if it's hours away, to check the text on the device.
3. Voice guide → user approval.
4. Phase 1 content + validation script + review sample.
5. Real-device test, with a Modo teste way to force a thought (no waiting 30+ min).
6. Phase 2 content.
7. Docs: rewrite this file as a real spec, update specs/overview.md,
   specs/sender-feature-cards.md and CLAUDE.md's index.
