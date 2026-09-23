# Brobot.Sender Internals — Pensamentos do MiMo (PLANNED, not implemented yet)

> Status: design agreed with the user, nothing built yet. This file is the
> plan to implement from; once the feature lands, rewrite it as the usual
> "how it works and why" spec and link it from CLAUDE.md,
> specs/overview.md and specs/sender-feature-cards.md.

A new checkbox card in Configurações Gerais, **"Pensamentos do MiMo"**. At
random, unpredictable moments through the day MiMo "thinks out loud" — a
short remark about the user's day (built from the Relatório data), the
moment (time, weekday, weather, games), a line from MiMo's own huge phrase
base, a translated random fact, or a "neste dia" historical fact. The goal is
to make MiMo feel like a living little creature, not a robot that only reacts.

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
  explicit clear. No firmware/PROTOCOL.md change.
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
15-min social/YouTube `NOTIFY` nudges stay as the neutral alert; thoughts
are the loose joke, at another moment.

## Sources and weights

| Class | Weight | Content |
|---|---|---|
| `WorkContextMessages` | 40% | ~8,200 phrases grouped by situation (see below), with `{min}`/`{site}`/`{temp}`/`{jogo}`/... placeholders |
| `MiMoBaseMessages` | 30% | ~10,000 standalone phrases in ~45 categories |
| `RandomFactsMessages` | 15% | uselessfacts.jsph.pl (EN) translated to PT-BR via MyMemory |
| `OnThisDayMessages` | 15% | Wikimedia feed `wikipedia/pt/onthisday/selected/MM/DD` (native PT-BR) |

`ThoughtDirector` draws a source by weight, never the same source twice in a
row, and if a source's `TryPick` returns `null` ("nothing fresh right now")
it passes the turn to the next source. MiMo is never silent: the local base
is the final fallback.

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
  they read like MiMo connecting things in its head, not reading a list.
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
too (report still shows the grouped total). MiMo never thinks *during* a game
(Game Mode screen); game remarks come after it closes or later in the day:
just finished playing, long single session (≥ 1h), daily game time ≥ 1h /
≥ 3h, gaming during business hours, several games today, first game of the
day — plus the combinations above.

### MiMoBaseMessages

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

## Architecture — `src/Brobot.Sender/Thoughts/`

```
Thoughts/
  IThoughtSource.cs        Name, Weight, Thought? TryPick(ThoughtContext)
  Thought.cs               Face, Text, Source, Category
  ThoughtContext.cs        snapshot: now, DailyReportResult, per-site/per-game minutes,
                           weather, current states (meeting just ended, last game...)
  WorkContextMessages.cs
  MiMoBaseMessages.cs
  RandomFactsMessages.cs
  OnThisDayMessages.cs
  ThoughtDirector.cs       which source speaks (weights, no repeats, pass-the-turn)
  ThoughtScheduler.cs      when (30 min + exponential, hold conditions, presence)
  ThoughtHistoryStore.cs   persisted shuffle queues, used phrases, cooldowns, today's fired triggers
  Data/
    mimo-base.tsv          embedded resource
    work-context.json      embedded resource, grouped by situation
```

- **Each source owns its rules and its own data file.** Phrases live in
  embedded data files, not C# arrays — fine for `PausaMessages`' dozens,
  unreadable at 18k lines, and the validation script shouldn't parse C#.
- A new source later = one class implementing `IThoughtSource`, registered
  in `ThoughtDirector` with a weight.
- `MainWindow` only wires the checkbox, builds the `ThoughtContext` from
  what it already has (`_dailyReport`, `_lastWeatherReading`, meeting/game/AI
  flags) and sends `FACE` + `MSG`.

## Content production

Total ~18,200 phrases — the heaviest part of the work, far more than the code.

- **Voice guide first** (user approves before any generation): first person,
  curious little robot creature, accomplice tone, ≤ ~75 chars.
- **Validation script**: length, glyphs supported by the font, exact and
  near-duplicate detection, placeholder rendering with max values, and a
  random sample per category for the user to review.
- **Phase 1**: ~1,000 base + ~1,500 context (every situation covered with
  smaller groups) — test the tone on the real MiMo.
- **Phase 2**: complete to full size with the voice guide adjusted from phase 1.

## Implementation order

1. Skeleton: `IThoughtSource`, `ThoughtScheduler`, `ThoughtDirector`,
   `ThoughtHistoryStore`, checkbox + `SenderSettings`, presence check,
   per-site and per-game tracking in `DailyReportTracker`.
2. API sources: `RandomFactsMessages`, `OnThisDayMessages`.
3. Voice guide → user approval.
4. Phase 1 content + validation script + review sample.
5. Real-device test, with a Modo teste way to force a thought (no waiting 30+ min).
6. Phase 2 content.
7. Docs: rewrite this file as a real spec, update specs/overview.md,
   specs/sender-feature-cards.md and CLAUDE.md's index.
