# Sistema de notificações — o que falta

Estado em 30/08/2026. O tier de notificação existe e funciona ponta a ponta,
mas só a Pausa foi migrada para ele e só o `COFFEE` tem tela própria. Este
arquivo lista o que ficou pendente, em ordem aproximada de importância.

Contexto de arquitetura: ver `PROTOCOL.md` (comando `NOTIFY`) e o comentário
de `Personality::Tier` em `BrobotCore/include/Personality.h`.

## O que já está pronto

- Tier `NOTIFICATION` no topo da escala, acima da IA:
  `NOTIFICATION > FOREGROUND (IA) > GAME > MEDIA`.
- Comando `NOTIFY <EXPRESSION> <texto>` — uma linha atômica, para a
  notificação de maior prioridade nunca ser pega meio aplicada entre um
  `FACE` e um `MSG`.
- Expira sozinha em 7s (`NOTIFICATION_DURATION_MS`) e o que estava embaixo
  reaparece sem nenhum reenvio, porque nenhum tier inferior é tocado.
- Tela dedicada: frame inteiro, sem olhos, sem selos, sem log, sem balão.
  Segue a paleta do tema ativo (`notificationPalette`).
- `GAME` separado de `MEDIA`, com `FACE IDLE_GAME` / `FACE IDLE_MEDIA`
  (o `FACE IDLE` puro segue limpando os dois, por compatibilidade).
- Pausa (Sender) migrada para `NOTIFY COFFEE`.
- Bedtime (Core) migrado para `raiseNotification(SLEEPY, ...)`.

Verificado com 11/11 checagens no protocolo cru contra o Core nativo, e
13/13 de regressão nos quatro temas.

---

## 1. Bug: vazados da xícara ficam pretos no Mi2-Mo2

**Confirmado por leitura do protocolo, não corrigido.**

`drawCoffeeCupAt` pinta os recortes (interior da caneca e o vão da alça) com
`BG_R/G/B`, ou seja, preto — o truque de "recortar um buraco" usado no
projeto inteiro assume fundo preto. O Mi2-Mo2 é o único tema cujo fundo não
é preto: a notificação lá limpa para a chapa clara do R2 (`226 227 231`), e
os recortes saem como dois buracos pretos no meio da xícara em vez de uma
caneca aberta.

Frame real capturado:

```
CLR 226 227 231
FILLRECT 56 38 40 30 26 42 96     <- corpo da xicara, navy
FILLRECT 58 41 36 26 0 0 0        <- interior: PRETO, deveria ser a chapa
FILLRECT 97 43 6 12 0 0 0         <- vao da alca: idem
```

**Correção:** dar a `drawCoffeeCupAt` parâmetros de cor de fundo e passar
`p.bgR/G/B` a partir de `drawCoffeeNotification`; o chamador do ícone de
canto continua passando `BG_R/G/B`. São ~4 linhas. Foi escrita e revertida
por não ter sido compilada/testada — não commitar sem build.

Vale checar se o mesmo padrão aparece em qualquer arte futura de
notificação: **toda arte nova precisa receber a cor de fundo, não assumir
preto.**

## 2. Clima ainda não foi migrado

`MainWindow.OnWeatherUpdated` continua no caminho antigo: manda
`FACE NEUTRAL` + `MSG <alerta>`, que cai no tier da IA, não no de
notificação. Ou seja, o alerta de mudança de clima **não** tem prioridade
máxima nem tela dedicada hoje.

Migrar para `NOTIFY`. Decidir qual expressão mandar — hoje as condições
(`CLEAR/CLOUDY/RAIN/STORM/SNOW/FOG`) não têm expressão correspondente, então
ou se escolhe uma existente por condição, ou o `NOTIFY` passa a aceitar um
token de clima. Ao migrar, dá para remover o comentário do `FACE NEUTRAL`
que só existia para forçar o roteamento de tier.

## 3. Só o COFFEE tem arte; o resto cai no card genérico

`drawNotificationScreen` despacha por expressão e tudo que não é `COFFEE`
usa `drawGenericNotification` — uma moldura arredondada vazia. Falta arte
para:

- **`SLEEPY`** (bedtime) — é a mais visível hoje, porque o Core já dispara
  esse nudge sozinho a cada 30 min durante a madrugada.
- **Clima**, depois do item 2 — provavelmente reaproveitando os pictogramas
  que `drawWeatherBadge` já desenha, em escala maior.

## 4. O bedtime deslocou o painel `SYSTEM NOTICE` do MiMo-84

Efeito colateral real da migração, notado no teste. O MiMo-84 tem um painel
próprio de sono (`drawMi84SleepNotice`: `SYSTEM NOTICE` / `HUMAN ACTIVITY:
LOW` / frase / prompt `>Z..Z..Z...`) que renderiza quando `SLEEPY` é a
expressão. Como o nudge agora sobe como notificação, durante os 7s de cada
nudge o que aparece é o **card genérico**, e o painel do tema só volta entre
um nudge e outro.

Decidir qual dos dois deve mandar. Opções: dar ao `SLEEPY` uma arte de
notificação por tema (o painel viraria a arte no MiMo-84), ou deixar o
bedtime fora do tier de notificação e voltá-lo ao comportamento anterior.

## 5. Notificação não faz som

`Buzzer::playForExpression` é disparado por *mudança de expressão* em
`main.cpp`, e `COFFEE` não tem cue mapeada — então a notificação entra muda.
Sendo a coisa de maior prioridade da tela, provavelmente deveria ter um som
curto. É uma entrada nova na tabela de `SoundSegment`.

## 6. Os 7s incluem o tempo de digitação

A mensagem digita a `TYPING_CHAR_INTERVAL_MS` (40ms/caractere) dentro da
mesma janela de 7s. Uma frase de 40 caracteres gasta ~1,6s digitando e sobra
~5,4s de leitura; frases mais longas sobram menos. Considerar começar a
contar os 7s **depois** que o texto termina de aparecer.

Relacionado: `NOTIFICATION_DURATION_MS` é fixa no firmware. Se for para
virar ajustável pelo usuário, precisa de comando próprio ou parâmetro no
`NOTIFY`.

## 7. Sem fila: uma notificação substitui a outra

`raiseNotification` sobrescreve a anterior e reinicia a janela. Duas
notificações quase simultâneas (ex.: Pausa às 22h batendo com o nudge de
bedtime) fazem a primeira sumir sem ter sido lida. Não decidimos se isso
precisa de fila — pode ser aceitável.

## 8. Toda notificação vai para o log da aba IA

`raiseNotification` chama `pushLogLine(text, LogTab::AI)`. Nos temas Matrix e
MiMo-84 isso mistura Pausa, clima e bedtime com a atividade da IA na mesma
aba. Funciona, mas polui. Alternativas: aba própria, ou não logar
notificações.

## 9. `CLR` duplicado por frame

`main.cpp` limpa o frame e `drawNotificationScreen` limpa de novo com a cor
do tema. Sai um `CLR` a mais por frame no protocolo (só afeta builds
`VSCREEN=1`, onde os draws viajam pela serial). Cosmético.

## 10. Versão do app não acompanha a do instalador

Não é do sistema de notificações, mas foi notado agora: subi
`MyAppVersion` para `1.1.0` no `installer/BrobotSenderSetup.iss`, mas o
csproj do `Brobot.Sender` não foi bumpado, então o .exe instalado continua
reportando `1.0.3.0` nas propriedades do Windows.

## 11. `.gitignore` não cobre este projeto

Também fora do escopo: o `.gitignore` da raiz é herdado de outro projeto
(tem regras de `xenia`) e não cobre PlatformIO nem a saída de publish do
.NET. Por isso `BrobotCore/.pio/build/**` e `BrobotCore/native/build/**`
estão versionados, e sobram `.tmp` e `libdeps/` soltos como não rastreados.
