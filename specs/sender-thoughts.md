# Brobot.Sender Internals — Pensamentos do Peemo

At random, unpredictable moments through the day Peemo "thinks out loud": a
remark about the user's day (built from the Relatório numbers), about the
moment (time, weekday, weather, a game that just closed), a line from its
own phrase base, a translated random fact, a "neste dia" historical event,
or a heads-up that the ISS, Tiangong or Hubble is about to cross the
user's sky. The point is to make Peemo feel like a small living creature,
not a robot that only reacts.

It follows **the one rule** (specs/overview.md): Sender picks *which text*
and *when*, exactly like `GreetingMessages`/`PausaMessages`/`WeatherAlerts`
— Core has no RTC, no report data and no network. Core still owns how it's
shown (plain foreground message, or the two space notifications).

Code: `src/Brobot.Sender/Thoughts/`. Data: `Thoughts/Data/*.tsv` (embedded
resources). Tone rules: [voice-guide.md](voice-guide.md) and
[mood.md](mood.md).

## Always on, no card

There is deliberately **no checkbox, no setting and no card** for this —
the user asked for it to be part of who Peemo is, not a feature to opt into.
Which *subjects* can come up still follows the other cards, the same gating
the Relatório uses: dev remarks need Ferramentas de Dev, social/YouTube/music
need Mídia, games need Jogos, meetings need Notificações (that's what
watches live calls).

## When — `ThoughtScheduler` + `MainWindow.TickThoughts`

- The gap to the next thought is **30 min + an exponential draw with a 50
  min mean**, capped at 4h — mean ~80 min, typically 4–9 thoughts in a 9h
  day. The 30-min floor is *added*, not clamped: clamping would pile every
  short draw onto exactly 30 min and make it predictable again. There's no
  daily cap (the distribution already limits it) and no quick follow-up.
  Nothing about the schedule is persisted; restarting Sender draws a fresh
  one.
- `TickThoughts` runs from the same 200ms `UpdateConnectionStatus` tick as
  everything else. A due slot is **held, not skipped**, while
  `ThoughtHoldReason` returns something — disconnected, a live call
  (`_activeCalls`), a game running (`_gameRunning`), Pong/RPG starting or
  running, AI text on screen (`_aiThoughtFaceActive` /
  `_aiMessageHoldUntil`), a full-screen notification in the last 2 minutes,
  or **the user away** (10+ min with no input anywhere, `UserPresence` /
  `GetLastInputInfo`). Peemo then thinks when the user comes back instead of
  talking to an empty room and wasting a phrase or a trigger.
- "Notification in the last 2 minutes" needs every full-screen line (NOTIFY,
  ACHIEVEMENT, REPORT) to go through `MainWindow.SendNotification`, which
  stamps `_lastNotificationSentAt`. New notifications must use it too.

## Which source — `ThoughtDirector`

| Source | Weight | What |
|---|---|---|
| `SpaceMessages` | priority | a real, visible ISS/Tiangong/Hubble pass |
| `WorkContextMessages` | 40 | remarks about the day and the moment |
| `PeemoBaseMessages` | 30 | Peemo's own standalone phrases |
| `RandomFactsMessages` | 15 | random fact, translated |
| `OnThisDayMessages` | 15 | "neste dia" from Wikipedia |

- Priority sources are asked **first** at every slot and only answer when
  something real is happening; they don't count as "last source".
- The weighted sources are tried in a weight-drawn order (without
  replacement) with the source that spoke last moved to the very end — so
  it's never the same source twice in a row unless nobody else has
  anything. A source returning `null` ("nothing fresh right now") just
  passes the turn. `PeemoBaseMessages` always has something, so Peemo is
  never silent.
- A new source is one class implementing `IThoughtSource` (plus
  `IBackgroundThoughtSource` if it fetches something), added to the array
  in `MainWindow`'s constructor.

## How it's shown

- Ordinary thoughts: `FACE <expr>` + `MSG <texto>` — a normal foreground
  message, **not** `NOTIFY` (a musing mustn't take over the screen). Core
  clears it by itself after the message has had its time and whatever tier
  was underneath (music, etc.) comes back. Never `FACE THINKING`: it's the
  one sticky foreground expression and would need an explicit clear.
- **Space thoughts** (`Thought.Face` = `SATELLITE` or `SPACE`) go out as
  `NOTIFY SATELLITE|SPACE <texto>` — the one kind of thought that takes the
  whole screen (see PROTOCOL.md → Notificações). Core opens both with
  "Transmissão Espacial Recebida!!!" before the text, so phrases never
  repeat "transmissão"/"recebi". SATELLITE is for satellites and stations,
  SPACE for everything else about space. Each source decides:
  - `SpaceMessages`: always SATELLITE.
  - `PeemoBaseMessages` / `WorkContextMessages`: the face column in the data
    file.
  - `RandomFactsMessages` / `OnThisDayMessages`: `SpaceTopic`, a
    whole-word keyword match — on the **English** text for facts, before
    translating (more reliable than the machine translation), on the
    Portuguese text for events.
- Every thought is logged to `%AppData%\Brobot\ai-events.log` as
  `Pensamento (<source>/<category or group>): <text>` — that's how a
  real-device test is checked.

## The sources

### PeemoBaseMessages — `Data/peemo-base.tsv`

Columns `id  category  face  level  text`. ~980 phrases in 45 categories
(existential, observations about humans, unanswerable questions, micro-poems,
world-domination plans, robot conspiracy theories, confessions, curiosities
about animals/body/history/food/ocean/plants/words/geography, space and
satellite curiosities, ...). The whole pool is shuffled once per install
into a persisted queue and walked in order, so nothing repeats until it has
all been used; phrases added to the file later join the queue at a random
spot. Never the same category twice in a row (relaxed only if that's the
only thing left). Phrases with a level (`leve`/`medio`/`acido`) are only
eligible in that mood; `-` is any mood.

### WorkContextMessages — `Data/work-context.tsv`

Columns `id  group  face  level  text`. Every phrase belongs to a
**situation group** and can only be said while that situation holds
(`Situations(context)`). When several hold, the most specific **tier** wins:

1. **Combinations** (42, all `combo:*`) — two things that hold at once:
   - weekday × something: `sexta-calor`, `segunda-chuva`, `sexta-noite`,
     `segunda-cedo`, `sexta-build-quebrado`, `segunda-sem-reuniao`,
     `sexta-sem-reuniao`, `sexta-noite-chuva`, `segunda-jogo`,
     `domingo-noite-jogo`, `fim-de-semana-commit` / `-reuniao` / `-jogo`;
   - time of day × something: `madrugada-commit` / `-jogo` / `-youtube` /
     `-rede-social` / `-musica` / `-reuniao` / `-frio` / `-calor` (all <5h),
     `manha-cedo-frio` / `-neblina` (5–8h), `almoco-jogo` / `-youtube`
     (12–14h), `tempestade-noite`;
   - weather × the day's numbers: `chuva-reuniao-longa`,
     `calor-reuniao-longa`, `calor-jogo-muito`, `chuva-jogo`, `chuva-youtube`;
   - the day's numbers × each other: `build-quebrado-jogo`,
     `reuniao-acabou-build-quebrado`, `dia-dev-perfeito` (≥5 commits and
     clean builds), `reuniao-longa-sem-commit`, `musica-build-limpo`,
     `rede-social-reuniao-longa`;
   - special date × something: `sexta-13-build-quebrado`, `natal-trabalho`,
     `ano-novo-jogo`, `inicio-mes-segunda`, `fim-mes-sexta`.
   Thresholds reuse the triggers' (rain/storm, ≥28° / <15°, ≥2h of calls,
   ≥3 failed builds, ≥1h of games...); the exact rules live in the
   "Combinations" block of `Situations`.
2. **Triggers and "just happened"** — `rede-social` / `rede-social-muito`
   (top site ≥30 / ≥90 min, fills `{site}` `{tempo}`), `youtube` /
   `youtube-muito` (≥30 / ≥90 min), `musica-longa` (media minus video ≥2h),
   `reuniao-longa` (≥2h of calls), `sem-reuniao` (weekday, ≥15h, no calls),
   `reuniao-acabou` (a call ended <20 min ago), `build-falhando` (≥3
   fails), `build-limpo` (≥5 builds, 0 fails), `commits-muitos` (≥5) /
   `commit-primeiro` (≥1), `jogo-acabou` (a game closed <20 min ago, fills
   `{jogo}`), `jogo-expediente` (that game closed on a weekday 9–18h),
   `jogo-hora` / `jogo-muito` (≥1h / ≥3h today), `jogos-varios` (≥3 games).
3. **Special dates** — `data:natal`, `ano-novo`, `halloween`, `sexta-13`,
   `dia-programador` (day 256), `inicio-mes`, `fim-mes`.
4. **General, always true** — `hora:*` (madrugada <5h, manha-cedo <8h,
   manha <12h, almoco <14h, tarde <18h, noite), `dia:*` (seg…dom), and
   `clima:*` from the last weather reading: the condition (`sol`,
   `nublado`, `chuva`, `tempestade`, `neblina`) plus `frio` (<15°) /
   `calor` (≥28°). A clear sky is `clima:sol` only from 6h to 18h and
   `clima:noite-limpa` otherwise — the first real-device test caught "tá sol"
   being said at night.

Rules around it:

- Triggers, dates and combos speak **once a day** each (`reuniao-acabou` and
  `jogo-acabou` once per event), tracked in `ThoughtHistory.FiredToday`. A
  met condition doesn't fire right away — it only gets priority at the next
  random slot, so timing stays unpredictable and it never lands on top of the
  15-min social/YouTube NOTIFY nudges (those stay the alert; the thought is
  the joke, at another moment).
- Within a tier, the group that spoke last is tried last
  (`ThoughtHistory.LastContextGroup`) — also from the real-device test,
  where one weather group spoke four times in two minutes.
- Each group walks its own persisted shuffle queue, same as the base.
- `{tempo}` renders as "45 min", "2h" or "2h30". Filled-in text goes through
  `ThoughtText.Accept`; a phrase that no longer fits (a very long game name,
  an undrawable character in it) is skipped for that situation.
- Per-site and per-game minutes come from `DailyReportTracker`, which tracks
  them (`SocialSecondsBySite`, `GameSecondsByName`) just for this — the
  Relatório still shows only the grouped totals.

### RandomFactsMessages — uselessfacts.jsph.pl + MyMemory

Free, keyless APIs (project rule: nothing metered or keyed). A stock of
translated facts lives in `thought-facts.json` and is refilled in the
background every 30 min when it drops below 5 (up to 8, at most 20
translations a day — far under MyMemory's ~5k anonymous chars/day, with 3s
between requests); `TryPick` only ever reads the stock. Facts are filtered
**before** translating: ≤70 chars in English (Portuguese runs longer) and a
blocklist for grim subjects, since uselessfacts does return some.
MyMemory's quota/error notices come back *as* the "translation", so those
and untranslated echoes are rejected. Translations can read a bit
machine-made; that's accepted.

### OnThisDayMessages — Wikimedia `onthisday`

Native PT-BR, free and keyless (Wikimedia asks for an identifying
User-Agent — `ThoughtHttp.Client` sends one with the repo URL). The day's
`selected` events are fetched once into `thought-onthisday.json`; if none
survives the filters, the much longer `events` list is the fallback (a
typical day has only ~3 selected, often grim). Filters: grim-subject
blocklist, and the rendered line — "Neste dia, em {ano}: {evento}." — must
pass `ThoughtText.Accept`. An event starting with an accented capital
(É, Ó...) is lowercased after the colon, since the font only has
lowercase accents. At most one a day. Ids use a stable FNV hash because
`string.GetHashCode` changes between runs on .NET.

### SpaceMessages — CelesTrak + SGP.NET

- Orbital elements (TLE) for ISS 25544, Tiangong 48274 and Hubble 20580
  only — famous ones; something generic passes every few minutes and would
  kill the magic. Fetched from CelesTrak at most once a day into
  `tle-cache.json`; a failed fetch keeps the previous elements. N2YO was
  rejected: its key would be shared by every install.
- Passes are computed locally with SGP.NET (`GroundStation.Observe`) at the
  location from `LocationProvider` — shared with `WeatherMonitor`, so it
  works with the Clima card off. No location, no passes.
- A pass qualifies when its max elevation is ≥30°, it's **visible to the
  naked eye** (sun ≤ −6° at the observer while the satellite is still
  sunlit — Earth's shadow modeled as a cylinder, sun from `Sun.Predict`)
  and it starts within the next 3h or is under way. The text gives the time
  and where to look (8-point compass at rise, in Portuguese), from a few
  templates; crewed stations get "tem gente lá" variants.
- Each pass is announced once, and at most one space thought a day.

## Text from the internet — `ThoughtText`

Folds typographic quotes, dashes, ellipses and `;` into what Font5x7 and
the physical display draw, rejects anything still undrawable, and caps
fetched text at 90 chars (a bit over the ~75 authored phrases aim for —
the message box scrolls).

## Memory and caches (`%AppData%\Brobot\`)

- `thought-history.json` (`ThoughtHistoryStore`): the shuffle queues,
  when each phrase id was last used, today's fired triggers and per-source
  counts, and the last source / category / context group. This is
  *Sender's* memory for not repeating itself — the phrases themselves still
  never imply Peemo remembers other days.
- `thought-facts.json`, `thought-onthisday.json`, `tle-cache.json`: the
  three fetch caches above.
- Ids in the data files are permanent: never reuse or renumber one, the
  history refers to them.

## Modo teste

- **Pensamento** makes a thought now through the normal director (hold
  conditions apply, except "notificação recente"), and shows what came out
  or what's blocking it.
- **Satélite** announces the next qualifying pass in the next 48h, even if
  it's hours away, straight to Peemo — it doesn't touch the history, so it
  doesn't use up the real one-a-day allowance.

## Content and `tools/validate-thoughts.py`

Run it before committing any change to either data file:

```
python tools/validate-thoughts.py              # errors fail, warnings are for review
python tools/validate-thoughts.py --sample 3   # also writes tools/thoughts-sample.md
```

It checks columns and unique ids, known faces/levels/groups, placeholders
per group, rendered length with the longest values ("Instagram", "10h30",
"League of Legends"...), characters the display can draw (uppercase accents
included — the font drops the accent), the voice guide's banned wording,
SATELLITE only on satellite phrases (space words elsewhere are a warning),
exact and near duplicates across both files, and that every context group
has phrases for every mood it can happen in (`GROUP_MOODS`). A new group
needs adding to `KNOWN_GROUPS` (and `GROUP_MOODS` / `GROUP_VALUES` when it
applies) as well as to `Situations`.

Size today: ~1,220 base phrases in 56 categories and ~3,315 context
phrases — the general groups (hora 109–148 each, dia 59–76, clima 49–74)
were grown first since they come up most, then the daily triggers (34–54
each), and 42 combos (11–27 each, after two growth passes); special dates,
once a year, have 9 each. The original
plan aimed at ~10,000 base and ~8,200 context; growing further is best done
in rounds after real use shows what repeats and what lands, with the voice
guide adjusted from that feedback. Weather is the hardest to grow without
near-duplicates — it needs new angles, not rewordings.

Not built from the original plan: a "long single game session" and "first
game of the day" trigger (daily totals are used instead), and a strict
one-month cooldown on trigger phrases (the per-group queues give the same
effect in practice).
