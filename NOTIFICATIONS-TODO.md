# Sistema de notificações — o que falta

Estado em 30/08/2026. O tier de notificação funciona ponta a ponta e o rosto
do MiMo aparece em todas as notificações. Pausa e aviso de dormir já estão
migrados e têm arte/animação própria; **o Clima ainda não foi migrado**.
Itens 1, 3 e 4 já foram resolvidos e ficam registrados por causa do que
ensinaram; o que resta começa no item 2 e no 5.

Contexto de arquitetura: ver `PROTOCOL.md` (comando `NOTIFY`) e o comentário
de `Personality::Tier` em `BrobotCore/include/Personality.h`.

## O que já está pronto

- Tier `NOTIFICATION` no topo da escala, acima da IA:
  `NOTIFICATION > FOREGROUND (IA) > GAME > MEDIA`.
- Comando `NOTIFY <EXPRESSION> <texto>` — uma linha atômica, para a
  notificação de maior prioridade nunca ser pega meio aplicada entre um
  `FACE` e um `MSG`.
- Expira sozinha em 10s (`NOTIFICATION_DURATION_MS`) e o que estava embaixo
  reaparece sem nenhum reenvio, porque nenhum tier inferior é tocado.
- Tela dedicada: frame inteiro, sem selos, sem log, sem balão — mas **com o
  rosto do MiMo sempre visível**. Segue a paleta do tema ativo
  (`notificationPalette`).
- `SLEEPY` tem animação própria (cochila, acorda de susto, pisca).
- `GAME` separado de `MEDIA`, com `FACE IDLE_GAME` / `FACE IDLE_MEDIA`
  (o `FACE IDLE` puro segue limpando os dois, por compatibilidade).
- Pausa (Sender) migrada para `NOTIFY COFFEE`.
- Bedtime (Core) migrado para `raiseNotification(SLEEPY, ...)`.

Verificado com 11/11 checagens no protocolo cru contra o Core nativo, e
13/13 de regressão nos quatro temas.

---

## 1. ~~Vazados pretos em fundo claro~~ (resolvido)

Era mais fundo do que parecia. `drawCoffeeCupAt` pintava os recortes com
`BG_R/G/B` (preto), o que quebrava no Mi2-Mo2, cujo fundo é a chapa clara do
R2 — a caneca saía com dois buracos pretos. Corrigido passando a cor de
fundo como parâmetro.

Ao colocar o rosto em todas as notificações, **a mesma classe de bug
apareceu um nível abaixo**: `fillRoundedRect` recortava os cantos dos olhos
também em preto, deixando quatro pontinhos escuros por olho sobre a chapa.
Só surgiu agora porque o Mi2-Mo2 nunca tinha desenhado olhos gêmeos — ele
desenha uma lente. Corrigido do mesmo jeito, com `BG_*` como valor padrão
para todos os chamadores existentes não mudarem.

**Regra que fica:** qualquer arte nova de notificação recebe a cor de fundo
por parâmetro. Não assuma preto.

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

Enquanto não migra, o alerta de clima aparece como mensagem normal — com
rosto, mas sem prioridade e sem os 10s.

## 3. ~~Card genérico ininteligível~~ (resolvido)

O fallback era uma moldura arredondada vazia, que não representava nada e
por isso não se entendia o que era. Foi removido inteiro. Agora **o rosto do
MiMo aparece em toda notificação** e a arte extra é o opcional, não o
contrário — então uma notificação sem ilustração própria mostra o MiMo
piscando com a mensagem embaixo, que já se explica sozinho.

Estado por expressão:

- **`COFFEE`** — olhos à esquerda e acima da linha de repouso da xícara,
  que fica à direita com vapor, mensagem embaixo. A cada ~2,8s a xícara sobe
  em direção ao rosto, segura e volta ao pires (o MiMo tomando um gole).
  A alça também foi corrigida: o recorte interno apagava a parede direita
  inteira, então ela era duas hastes sem nada fechando o laço.
- **`SLEEPY`** — os olhos *são* a animação (ver item 4).
- **Qualquer outra** — rosto centralizado piscando normalmente.

Falta arte dedicada só para o clima, junto do item 2 — provavelmente
reaproveitando os pictogramas que `drawWeatherBadge` já desenha, em escala
maior, ao lado do rosto.

## 4. ~~Bedtime sem animação própria~~ (resolvido)

O nudge de sono agora tem animação própria, em ciclo de 3,6s que roda ~2x
dentro dos 10s: os olhos **caem devagar** como quem cochila (curva quadrática,
quase parados no começo e depois cedendo), ficam ~300ms quase fechados,
**abrem de susto** ultrapassando o tamanho normal, e então **piscam três
vezes** rápido antes de recomeçar.

Ancorada em `FaceState::notificationStartedMs`, não em `nowMs` solto, para o
ciclo sempre começar acordado — de um relógio livre ele poderia abrir no
meio da queda, o que lê como falha e não como sono.

Medido no protocolo: altura do olho 39 → 4 px na queda, salto para 44 px no
susto (repouso é 39), e as três piscadas fechando em 14, 4 e 13 px.

**Continua em aberto:** o painel `SYSTEM NOTICE` do MiMo-84
(`drawMi84SleepNotice`) segue existindo para quando `SLEEPY` é a expressão
*fora* de uma notificação. Durante os 10s do nudge quem manda é a animação.
Decidir se o painel do tema deveria ser a arte do MiMo-84 nessa notificação,
em vez dos olhos genéricos.

## 5. Notificação não faz som

`Buzzer::playForExpression` é disparado por *mudança de expressão* em
`main.cpp`, e `COFFEE` não tem cue mapeada — então a notificação entra muda.
Sendo a coisa de maior prioridade da tela, provavelmente deveria ter um som
curto. É uma entrada nova na tabela de `SoundSegment`.

## 6. A janela inclui o tempo de digitação (atenuado)

A mensagem digita a `TYPING_CHAR_INTERVAL_MS` (40ms/caractere) dentro da
mesma janela da notificação. A janela subiu de 7s para 10s, o que dá folga
suficiente na prática — uma frase de 40 caracteres gasta ~1,6s digitando e
sobram ~8,4s de leitura. Segue valendo que frases muito longas comem mais
tempo; se voltar a incomodar, a correção é começar a contar **depois** que o
texto termina de aparecer, em vez de aumentar a janela de novo.

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
