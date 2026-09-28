# Peemo's mood

Closely tied to [sender-thoughts.md](sender-thoughts.md), whose phrases are
its biggest user, and to [voice-guide.md](voice-guide.md), which defines
what each level may and may not say.

Peemo gets a mood that follows the clock through the day. It sets **how
sarcastic every phrase Peemo says is**, and it shows on screen as a small
badge. The goal is the same as Pensamentos': Peemo feels like a little
creature whose day wears on, not a random-phrase machine.

## The three moods

| Mood | Hours | Sarcasm level used |
|---|---|---|
| `ANIMADO` | 07:00–15:59 | **leve** only |
| `FIM_DE_DIA` | 16:00–21:59 | **médio** only |
| `CANSADO` | 22:00–06:59 | **ácido** only |

- Boundaries are `[start, end)` on the local clock; same on weekends.
- Every level still obeys the voice guide: Peemo mocks the situation, never the person; accomplice, never
  supervisor. **Ácido is the sharpest wording, not a license** — no orders
  ("volta a trabalhar"), no threats (even joking "vou contar pro seu
  chefe"; "não vou contar" is fine), no judging the person, no swearing,
  no appearance/weight/health, no heavy subjects, never implying memory of
  other days ("de novo", "como sempre"). The validation script checks for
  these.
- Examples agreed with the user (one per level, same situation):
  - leve: "Facebook tá rendendo hoje, hein"
  - médio: "2h de Facebook hoje. Se perguntarem, era pesquisa de mercado"
  - ácido: "2h de Facebook. Nem o Zuckerberg passa tanto tempo lá"

## Who decides (the one rule)

Mood is personality, so **Core owns it**: `Personality` derives it from
`_timeText` — the same `TIME`-fed field `isBedtimeHour` already uses for
`SLEEPY` — and `Face` draws the badge. **No new wire command.**

Sender still picks the *text* (Core has no phrases, no report data), so it
needs the mood too: a tiny `PeemoMood.At(DateTime)`/`PeemoMood.Current` helper with the
same three thresholds. Both copies are fixed constants that must match
(comment in each pointing at the other); they can't drift apart in
practice because Core's clock *is* Sender's clock — `TIME` comes from the
same machine.

- No `TIME` received yet (Hora card off) → Core shows no mood badge, same
  as it shows no clock badge. Sender still tones its phrases by its own
  clock.
- **The existing 22h `SLEEPY` bedtime animation stays exactly as it is** —
  `CANSADO` doesn't touch eyes or blink, it only adds the badge and sets
  the phrase level.

## The badge

Top of the frame, **centered between the weather badge (left) and the
clock (right)** — the free x-range there is roughly 38–122px at the
default 18C / "22:15" widths.

- A mini robot **battery** (`drawMoodBattery` in Face.cpp): 4 cells for
  `ANIMADO`, 2 for `FIM_DE_DIA`, one cell blinking slowly for `CANSADO`.
  Procedural like every other badge (no bitmap); its outline is four rects
  rather than a hollowed fill, so it works on P2-M2's light plate too.
- Same color as the weather/clock badges and drawn alongside them. Peemo-84
  has no pictogram badges (its status row is text and already near full
  width), so there the battery sits right-aligned on the header row, after
  "PEEMO SYSTEM v2.6".
- When looking up, the eyes can graze the badge's edges for a moment — it
  sits mostly in the gap between the eyes; accepted on the device.

## Every phrase gets a level

"Tudo que tem frase" — mood applies to **all** of Peemo's own phrases, not
just Pensamentos:

| Where | How it follows the mood |
|---|---|
| `GreetingMessages` (connect greeting + disconnect farewell) | time buckets, each written at its mood's level; the 12–18h bucket split at 16h |
| `PausaMessages` (Pausa, `NOTIFY COFFEE`) | a `MoodPhrases` pool per level |
| `WeatherAlerts` (Clima, `NOTIFY WEATHER`) | per condition × level |
| `AlertMessages` — social/YouTube 15-min nudges, CPU/RAM alerts | a pool per level; always keeps the `{min}`/`{percent}` number |
| `DailyReportMessages` (Relatório) | per rating × level |
| Core's `BEDTIME_MESSAGES` (`SLEEPY`) | always `CANSADO`, so written at ácido; no clock times in the text (it repeats every 30 min) |
| Pensamentos | a `level` column in both data files (see sender-thoughts.md) |

Not mood-toned: AI activity text (it's Claude's text, not Peemo's), the
achievement names, and fixed labels like "Meet: <título> <hora>".

**Neutral content has no level and is used in every mood**: random facts,
"neste dia", space passes, and the non-joking base categories
(curiosities, micro-poems, unanswerable questions...). Only phrases that
joke *about the user's situation* carry a level.

### Content volume

Splitting pools by level would cut each one's lifetime by three, so size
per level by **how often that mood actually comes up**:

- Time-of-day situations fall inside one mood already (manhã → leve,
  noite → médio, madrugada → ácido): no extra phrases, just the right tone.
- Situations that can happen at any hour (weekday, weather, report
  triggers, games, special dates, combinations) need all three levels.
  Most PC time is daytime, so leve gets the largest share, then médio,
  then ácido.
- Data files get a `level` column (`leve`/`medio`/`acido`/`-` for neutral);
  the validation script rejects a situation group missing a level it can
  occur in.
