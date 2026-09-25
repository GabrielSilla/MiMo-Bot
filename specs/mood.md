# Peemo's mood (in progress)

> Status: steps 1–2 done (Core badge + PeemoMood; every existing phrase
> list converted to MoodPhrases pools). Step 3 comes with Pensamentos. Once it lands,
> rewrite this as the usual "how it works and why" spec and link it from
> CLAUDE.md and specs/overview.md. Closely tied to
> [sender-thoughts.md](sender-thoughts.md), whose phrases are the biggest
> user of it.

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
- Every level still obeys the tone rules in sender-thoughts.md ("Tone"):
  Peemo mocks the situation, never the person; accomplice, never
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

- Proposal to review on the device: a mini robot **battery** — full for
  `ANIMADO`, half for `FIM_DE_DIA`, low and slowly blinking for `CANSADO`
  — optionally with the mood name next to it if it fits. Procedural like
  every other badge (no bitmap).
- Same color and themes as the weather/clock badges: drawn wherever they
  are (`drawWeatherBadge`/`drawClockBadge` in Face.cpp), hidden where they
  are hidden.

## Every phrase gets a level

"Tudo que tem frase" — mood applies to **all** of Peemo's own phrases, not
just Pensamentos:

| Where | Today | With mood |
|---|---|---|
| `GreetingMessages` (connect greeting + disconnect farewell) | by time of day | by mood (time-of-day grouping largely lines up already) |
| `PausaMessages` (Pausa, `NOTIFY COFFEE`) | one list | one pool per level |
| `WeatherAlerts` (Clima, `NOTIFY WEATHER`) | per condition | per condition × level |
| Social/YouTube 15-min nudges (`NOTIFY NEUTRAL`) | one fixed sentence each | a small pool per level; still an alert, `{min}` placeholder kept |
| Resource alerts (`NOTIFY SWEATING`) | fixed CPU/RAM sentences | a small pool per level |
| `DailyReportMessages` (Relatório) | by rating | by rating × level |
| Core's `BEDTIME_MESSAGES` (`SLEEPY`) | 10 fixed phrases | always `CANSADO` → rewritten at ácido level, still no orders |
| Pensamentos (all sources) | — | see below |

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
  then ácido. Exact numbers are set in the voice-guide step, from the
  real hours the user is connected.
- Data files get a `level` column (`leve`/`medio`/`acido`/`-` for neutral);
  the validation script rejects a situation group missing a level it can
  occur in.

## Implementation order

1. `PeemoMood` in Sender + mood derivation and badge in Core (small, testable
   on its own with `TIME` from the Modo teste).
2. Existing phrase lists converted to per-level pools (content: voice guide
   with per-mood examples first, user approves, then write).
3. Pensamentos picks phrases by mood (folded into sender-thoughts.md's own
   order: skeleton reads `PeemoMood`, content phases write per level).
4. Docs.
