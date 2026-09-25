# Peemo's voice guide (approved)

> The rules every phrase Peemo says is written and validated against: the
> mood conversion of the existing lists ([mood.md](mood.md)) and all of
> Pensamentos' content ([sender-thoughts.md](sender-thoughts.md)). Approved
> by the user; the existing lists were converted against it.

## Who Peemo is

A curious little robot creature that lives on the desk. Speaks in the
**first person**, casual PT-BR, like a buddy who's in on it with the user.
**Accomplice, never supervisor**: it notices things and jokes about them,
it doesn't manage anyone's day.

- Mocks **the situation**, never the person.
- Only knows **right now**: never implies memory of other days — no "de
  novo", "como sempre", "essa rotina", "sua mania", "ontem você...".
  "Amanhã" is fine (future, not memory).
- No gushing/forced praise ("adorei", "gostei", "amei").
- It's a robot and plays with it: bateria, modo economia, circuitos,
  compilador, sensores — the mood badge being a battery helps here.

## Format

- **≤ 75 characters.** Notification text wraps at 26 chars × 3 lines;
  longer scrolls, which reads worse.
- **Characters the font draws**: `A-Z a-z 0-9`, space, `. , ! ? ' - : / % ( ) [ ] _ # >`
  and the lowercase accents `á à â ã é ê í ó ô õ ú ç`.
  - **Accents are welcome** (new and rewritten phrases use them — the
    current lists are plain ASCII only because they predate the accent
    glyphs).
  - **No uppercase accents** (É, À, Ç...): the font falls back to the bare
    letter, so a phrase starting "É" shows "E". Rephrase instead.
  - Not drawable, never use: `; " + * = & @ $ < ~ …`, emoji, `ü`.
- Placeholders (`{min}`, `{site}`, `{percent}`, `{temp}`, `{jogo}`...) are
  validated with their **longest** value ("TikTok", "180", "100", the
  longest game name) so the rendered line still fits.
- "kkkk" is allowed but rare (at most one in ~30 phrases of a pool).

## Never, at any level

- Orders about work or leisure: "volta a trabalhar", "chega de YouTube",
  "fecha isso".
- Threats, even joking: "vou contar pro seu chefe". The reverse — "não vou
  contar pra ninguém" — is fine: that's being an accomplice.
- Judging the person: "preguiçoso", "que falta de foco", "você é lento".
- Swearing, appearance/weight/health, heavy subjects (death, crime,
  disease, politics, religion).
- Memory of other days (see above).

**Reminder cards keep their reminder.** Pausa ("levanta, pega um café"),
Clima ("leva guarda-chuva"), hora de dormir and CPU/RAM exist *to* suggest
something, so they still do — that's not policing. The mood changes how
it's said, never whether it's said.

## The three levels

| Mood | Level | What it sounds like |
|---|---|---|
| Animado (07–16h) | **leve** | Good-humored, full battery. A friendly nudge, the joke is gentle and mostly on Peemo itself or the moment. |
| Fim de Dia (16–22h) | **médio** | A bit tired, drier. Teases the situation with a wink; "we're in this together". |
| Cansado (22–07h) | **ácido** | Low battery, grumpy-funny. The sharpest wording, deadpan, but still on the user's side and still inside the "never" list. |

Neutral content (random facts, "neste dia", satellite passes, curiosities,
micro-poems) has **no level** and is used in any mood — only phrases that
joke about the user's situation carry one.

## Examples per card (same situation, three moods)

**Saudação ao conectar** (`NOTIFY BYE`) — the existing time buckets map
onto the moods (7–16h Animado, 16–22h Fim de Dia, 22–7h Cansado):
- leve: "Bom dia! Bateria cheia aqui, bora ver o que o dia apronta"
- médio: "Ligando às 17h? Chegou pro segundo tempo, hein"
- ácido: "23h e você me ligando. Espero que seja importante"

**Despedida** (`NOTIFY BYE`):
- leve: "Já vai? Beleza, fico aqui de guarda"
- médio: "Fechou por hoje? Justo, o dia rendeu"
- ácido: "Finalmente. Minha bateria agradece"

**Pausa** (`NOTIFY COFFEE`):
- leve: "Pausa pro café! Estica as pernas que eu seguro as pontas"
- médio: "Pausa. Mais um café e esse dia termina rapidinho"
- ácido: "Pausa a essa hora? Café agora só se for descafeinado"

**Clima — chuva** (`NOTIFY WEATHER`):
- leve: "Vai chover! Guarda-chuva na mochila e tá tudo certo"
- médio: "Chuva chegando bem na hora de ir embora, que timing"
- ácido: "Vai chover. Ótimo motivo pra não sair de perto de mim"

**Redes sociais, 15 min** (`NOTIFY NEUTRAL`, still an alert):
- leve: "15 min de rede social! Só avisando, sem julgamento"
- médio: "15 min de rede social. O feed não acaba, já te adianto"
- ácido: "15 min de rede social às 23h. O algoritmo agradece"

**YouTube, 15 min** (`NOTIFY NEUTRAL`):
- leve: "15 min de YouTube! Deve estar bom esse vídeo"
- médio: "15 min de YouTube. Mais um e vira maratona"
- ácido: "15 min de YouTube. O 'só mais um' tá ganhando de lavada"

**CPU/RAM alta** (`NOTIFY SWEATING`):
- leve: "CPU em 95%! Tô suando aqui, dá uma aliviada?"
- médio: "CPU em 95%. Tô trabalhando mais que muita gente hoje"
- ácido: "RAM em 95%. Fecha umas abas, eu não sou de ferro"

**Relatório do dia — dia ruim** (`REPORT`):
- leve: "Dia meio travado, mas amanhã tem outro"
- médio: "Dia fraco. Nem o compilador quis colaborar"
- ácido: "Dia fraco. Vou fingir que não vi esses números"

**Hora de dormir** (Core's `BEDTIME_MESSAGES`, always Cansado → ácido only):
- "22h. Eu já tô em modo economia, você devia pensar nisso"
- "Tá tarde. Seu travesseiro tá se sentindo ignorado"

**Pensamento** (Pensamentos do Peemo, report trigger):
- leve: "Facebook tá rendendo hoje, hein"
- médio: "2h de Facebook hoje. Se perguntarem, era pesquisa de mercado"
- ácido: "2h de Facebook. Nem o Zuckerberg passa tanto tempo lá"

**Space thoughts** (`NOTIFY SATELLITE`/`SPACE`) come right after Core's
"Transmissão Espacial Recebida!!!" line, so they read as the transmission
itself and never say "transmissão"/"recebi":
- "A ISS passa às 19:42 bem em cima de você. Acena, vai que eles veem"

## Existing lists — what changes

| List | Today | After |
|---|---|---|
| `GreetingMessages` connect | 7 time buckets × ~50 | 8 buckets × ~30–40, re-toned to their mood; 12–18h split at 16h |
| `GreetingMessages` disconnect | 3 buckets × ~50 | 07–16h leve, 16–22h médio, 22–07h ácido; 30 each |
| `PausaMessages` | 10 | 20 leve, 20 médio, 10 ácido |
| `WeatherAlerts` | 10 per condition | 10 per condition per level |
| Social/YouTube nudges | 1 fixed each | 10 per level each |
| Resource alerts (CPU/RAM) | 1 fixed each | 8 per level each |
| `DailyReportMessages` | ~12 per rating | 8 per rating per level |
| Core `BEDTIME_MESSAGES` | 10 | 15, ácido |

One existing phrase breaks these rules today and goes away in the same
pass: bedtime's "vai virar a noite de novo?" (memory of other days).
