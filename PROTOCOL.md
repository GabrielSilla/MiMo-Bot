# Brobot Serial Protocol

Fonte única de verdade para o protocolo de texto usado na comunicação Serial
entre o **Brobot Core** (Arduino/ESP32) e o **Brobot Virtual Display** (PC).

Ambos os lados devem se manter sincronizados com este documento. Resolução
lógica: sempre **160x128** (paisagem).

Formato geral: uma linha ASCII por comando, terminada em `\n`, campos
separados por espaço. Parâmetros de cor são sempre `r g b` (0-255).

## Comandos de controle (host → Brobot Core)

Enviados via Serial Monitor ou por um script de teste no PC, para o Arduino.

| Comando        | Descrição                                              |
|----------------|----------------------------------------------------------|
| `FACE <nome>`  | Define a expressão. Valores: `NEUTRAL`, `HAPPY`, `SAD`, `ANGRY`, `SLEEPING`, `SLEEPY`, `COFFEE`, `MUSIC`, `WATCHING`, `ERROR`, `READING`, `FINISHED`, `THINKING`, `PLAYING`, `IDLE` |
| `MSG <texto>`  | Define o texto exibido abaixo dos olhos (resto da linha). `MSG` sem texto limpa a mensagem. |
| `WEATHER <tempC> <condicao>` | Selo persistente de clima (canto superior esquerdo). `tempC` é inteiro (pode ser negativo). Condições: `CLEAR`, `CLOUDY`, `RAIN`, `STORM`, `SNOW`, `FOG`. `WEATHER` sem argumentos limpa o selo. |
| `TIME <HH:MM>` | Relógio persistente (canto superior direito). Core não tem RTC nem rede própria — quem envia isso é o app PC conectado. `TIME` sem texto limpa o relógio. |
| `THEME <nome>` | Estilo visual do display inteiro. Valores: `DEFAULT` (olhos + balão de mensagem, o padrão), `MATRIX` (olhos menores no canto inferior + log estilo console em verde no topo, ver abaixo), `MI2MO2` (a tela inteira vira um close da cúpula do R2D2, ver abaixo) ou `MI84` (terminal CRT âmbar de 1984, ver abaixo). Persistente, como `WEATHER`/`TIME` — fica valendo até o próximo `THEME` chegar. |
| `SOUND <ON\|OFF>` | Liga/desliga os sons do buzzer (bipes R2D2 por expressão, ver Buzzer.cpp). Persistente — fica valendo até o próximo `SOUND` chegar. Padrão do Core: `ON`. Texto não reconhecido é ignorado (mantém o valor atual). |
| `SCANLINES <ON\|OFF>` | Liga/desliga o filtro CRT completo da tela física (scanline rolante + chromatic fringing + tint quente + vinheta — ver ST7735PhysicalDisplay.cpp). Sem efeito no Brobot Virtual Display/build nativo, que nunca aplicam esse pós-processamento. Persistente — fica valendo até o próximo `SCANLINES` chegar. Padrão do Core: `ON`. |
| `STATS <cpu%> <cpuTempC> <gpu%> <gpuTempC> <ram%>` | Carga da máquina, para o Game Mode (ver abaixo). Todos inteiros; **-1** em qualquer campo significa "o app do PC não conseguiu essa medida" e é desenhado como `--`. `STATS` sem argumentos limpa. Persistente como `WEATHER`/`TIME` — e, como eles, **não conta como interação**: chega a cada 2s enquanto um jogo está aberto, e se contasse o MiMo nunca mais dormiria. |
| `NOTIFY <EXPRESSÃO> <texto>` | Levanta uma **notificação**: a maior prioridade do display, acima até da IA. Toma a tela inteira por 10s com uma animação dedicada e some sozinha (ver abaixo). Uma linha só, atômica, de propósito. |
| `PING` | **O único comando que o Core responde** — devolve a linha `MIMO <revisão>` (hoje `MIMO 1`) para quem perguntou. Não mexe em nada: não é sobre o Brobot, é sobre o link. Existe para o app PC conseguir *achar* o MiMo na rede (ver abaixo). |

`WEATHER`, `TIME`, `THEME`, `SOUND` e `SCANLINES` são independentes de `FACE`/`MSG`: não interrompem nem são interrompidos por eles, não "expiram" sozinhos, e ficam visíveis/valendo até o próximo comando do mesmo tipo substituí-los.

### Game Mode

Quando o Brobot.Sender detecta um jogo, ele já manda `FACE PLAYING` + `MSG
Jogando <nome>`; agora manda `STATS` junto, a cada 2 segundos. O Core decide
o que fazer com isso — o app do PC não escolhe tela nenhuma, só fornece os
números, igual ao que faz com clima e hora.

O que aparece depende do tema:

- **`MATRIX`**: uma terceira aba no log, `IA MIDIA [MONITOR]`. Ela mostra o
  nome do jogo (com o mesmo prefixo `> ` e a mesma quebra de linha de
  qualquer entrada do log) e, embaixo, três linhas: `CPU 25% 53C`,
  `GPU 12% 38C`, `RAM 66%`. O nome do jogo saiu da aba MIDIA e passou a
  viver aqui — música e vídeo continuam na MIDIA, que não tem stats.
- **`MI84`**: mesma terceira aba `IA MIDIA [MONITOR]` do `MATRIX` (é o
  mesmo `LogTab`, não uma cópia), mas os números viram medidores de barra:
  uma linha por leitura, com rótulo, dez células, o percentual e a
  temperatura — `CPU [######····] 43% 61C`. As células são retângulos
  desenhados, não caracteres de bloco: a Font5x7 não tem glifo de bloco, e
  nesse tamanho um retângulo sai mais nítido. As três linhas ficam em `y`
  fixo, então o nome do jogo quebrando em mais ou menos linhas não empurra
  os números — mesma razão pela qual `DEFAULT`/`MI2MO2` ancoram os deles na
  base da caixa.
- **`DEFAULT` e `MI2MO2`**: a caixa de mensagem cresce e vira um painel fixo,
  com um medidor por linha (`CPU`, `GPU`, `RAM`) e o nome do jogo acima
  deles. Ela **não digita e não expira** — é um mostrador atualizado a cada
  2s, e redigitar a cada atualização deixaria os números ilegíveis.
  No `DEFAULT` a caixa tem 5 linhas: os olhos encolhem e sobem para o topo
  da tela enquanto o jogo roda, liberando espaço, e o nome do jogo pode
  ocupar duas linhas. No `MI2MO2` são 4 linhas e a chapa do R2 fica
  intocada — a lente termina em y=77 e uma caixa de 5 linhas começaria em
  y=71, cobrindo o olho dele; com 4 linhas ela começa em 80 e passa raspando.
  Em ambos, os três medidores ficam ancorados na base da caixa, então os
  números não se mexem conforme o nome do jogo quebra em mais ou menos
  linhas.

Os stats só são desenhados enquanto `PLAYING` é a expressão em cena. Um
`STATS` recebido fora disso é guardado e simplesmente não aparece, do mesmo
jeito que um `WEATHER` chega antes de haver espaço pra ele.

### `PING` e a descoberta do MiMo na rede

O IP do MiMo vem do DHCP do roteador, então ele **muda sozinho** — um
desligar/ligar (do MiMo ou do roteador) pode devolver um endereço diferente,
e o endereço salvo no card Conexão do Brobot.Sender passa a apontar pra nada.
Quando isso acontece, o Sender varre a rede local procurando o MiMo
(`MimoDiscovery`, ver CLAUDE.md): abre uma conexão em cada host da(s) sub-rede(s)
do próprio PC, e em cada um que aceitar a conexão manda `PING`.

Só quem responde `MIMO` é adotado. Isso não é preciosismo: a porta 5555 não é
reservada pra este projeto (o ADB-over-network do Android, entre outros, usa a
mesma), e na própria rede onde isso foi desenvolvido havia um aparelho não
relacionado escutando nela — uma varredura do tipo "primeira porta aberta
vence" teria adotado esse aparelho como se fosse o MiMo e passado a mandar
`FACE`/`MSG` pra ele. A única exceção é o endereço que **já estava sendo
usado**: se ele continua aceitando conexão mas não responde `PING`, é assumido
como MiMo mesmo assim (placa com firmware anterior ao `PING`).

Consequência prática: **achar um MiMo que mudou de IP exige o firmware com
`PING`**. Sem ele, a varredura só consegue reconfirmar um endereço que já
funcionava.

**`THEME MATRIX`**: recolore tudo em verde e troca o balão de mensagem por um log estilo terminal no topo da tela (linhas prefixadas com `> `, mais antigas saem conforme novas entram — até 6 entradas). Toda mensagem que normalmente apareceria no balão (IA, mídia, jogos, pausa, boa-noite) vira uma linha nova nesse log. Os selos de clima e relógio **não** são trocados — continuam fixos nos cantos superiores exatamente como no `DEFAULT`, e o log começa abaixo deles. Como os dois já ficam visíveis o tempo todo, o log não carrega nenhuma linha periódica de hora/clima (uma versão anterior deste tema carregava, de quando os selos eram suprimidos aqui e o log era a única forma de ver essa informação). Puramente visual — os comandos `FACE`/`MSG`/`WEATHER`/`TIME` continuam funcionando exatamente igual por baixo, só a forma de desenhar muda.

**`THEME MI2MO2`**: a tela inteira deixa de ser um rosto no preto e vira um *close* da chapa da cúpula do R2D2 — fundo claro cobrindo o frame, painéis azul-marinho embutidos, a lente preta grande do fotorreceptor e, à direita dela, o painel lógico (a "bolinha") e um vent prateado. Diferente dos outros temas, **nenhuma expressão muda a forma do olho**: a lente é um disco preto fixo, que não pisca, não espreme e não muda de cor. Quem carrega tudo é a bolinha — que é como o R2 de verdade se expressa, piscando painéis em vez de mexer o olho (que nele é um vidro parado):

| Estado | Bolinha |
|---|---|
| repouso | vermelho vivo, piscando (apaga e volta) no lugar da piscada do olho |
| `THINKING` | pisca de forma intermitente e irregular |
| `ERROR` | três flashes em vermelho vivo, depois fica acesa |
| `FINISHED` | verde |
| `SLEEPING` | apagada |
| `COFFEE` | sem animação — só a mensagem (a xícara não é desenhada neste tema) |

O olhar em volta também é diferente: em vez de deslocar o olho, desliza o ponto branco de reflexo pela lente — numa lente preta lisa, o reflexo é a única coisa capaz de mostrar que ela girou. A diagonal superior-esquerda é excluída do sorteio de direções neste tema (é a única cujo deslocamento levaria o reflexo pra fora do vidro). Selos de clima/hora ficam azul-marinho (o fundo claro apaga as cores dos outros temas) e os ícones de canto (música/vídeo/livro/jogos/café/"Z Z Z") ficam vermelhos como a bolinha. O balão de mensagem continua cinza escuro com texto branco, igual ao `DEFAULT`.

Referência ao R2D2 no texto: cada caractere da mensagem, assim que revelado pelo efeito de digitação normal, aparece por um instante em **Aurebesh vermelho** antes de virar a **fonte latina branca** — fonte e cor trocam juntas, no relógio individual de cada caractere, então a frase mostra uma "frente de decodificação" atravessando a linha, com o começo já resolvido em branco e o fim ainda alienígena em vermelho. Funciona tanto no Brobot Virtual Display/build nativo (fonte bitmap própria, `AurebeshFont.cs`) quanto no firmware físico (ST7735, `GFXfont` própria, `AurebeshGFXFont.h`) — cobre só `A-Z`/`0-9` nos dois lados; qualquer outro caractere (espaço, pontuação, minúsculas/acentos) sempre desenha na fonte latina, mesmo que `AUREBESH` seja pedido.

**`THEME MI84`**: um terminal CRT âmbar de 1984 — preto e uma única cor de
tinta (âmbar `255,176,0`, mais um âmbar escuro só para o "cromo": réguas,
rótulos e células apagadas das barras). É o oposto do `MI2MO2`: em vez de
substituir o rosto, ele substitui a *moldura*. O topo da tela vira um
cabeçalho fixo de terminal e os olhos do `MATRIX` (pequenos, presos na base
do frame) continuam embaixo, com as mesmas formas por expressão.

```
MIMO SYSTEM v2.6
TIME 17:42  RAIN 18C
------------------------
[IA] MIDIA MONITOR
------------------------
> Executando comando...
> Analisando o
repositorio pra achar o
problema
>THINKING_

        ( o )  ( o )
```

Reaproveita o log e as abas do `MATRIX` inteiros — mesmo `logLines`, mesmo
`LogTab`, mesma lógica do lado do `Personality` — então "qual aba está
ativa" e "o que uma mensagem da IA faz com o log" são decididos num lugar
só, para os dois temas. Consequência: a prioridade automática das abas (IA
ganha de mídia, que ganha de nada) não precisou de código novo em lugar
nenhum; ela já cai fora do fato de o tier foreground vencer o background.

Diferenças próprias em relação ao `MATRIX`:

- **Hora e clima viram texto, não selos.** As duas leituras dividem uma
  única linha (`TIME 17:42  RAIN 18C`) e os pictogramas são suprimidos —
  seria a mesma informação duas vezes, numa faixa que o cabeçalho já ocupa.
  A condição é impressa com o mesmo nome do token de `WEATHER`
  (`CLEAR`/`CLOUDY`/`RAIN`/`STORM`/`SNOW`/`FOG`), em vez de um segundo
  vocabulário que poderia divergir do que é enviado. Sem símbolo de grau: a
  Font5x7 não tem, e o selo de clima já imprime `18C` seco pelo mesmo motivo.
- **`THINKING` não usa os olhos glitchados.** Aqui ele é carregado por duas
  coisas: os olhos piscando de forma intermitente e irregular (o mesmo
  stutter que a bolinha do `MI2MO2` já usava) e uma linha de prompt
  `>THINKING_` que se digita letra por letra, apaga e recomeça enquanto a
  IA estiver pensando. Essa linha ocupa **só a última linha** da aba IA — o
  log continua rolando acima dela, de modo que os rótulos de ferramenta que
  o hook manda (`Executando comando...`) continuam visíveis, exatamente como
  no `MATRIX`.
- **Os dois estados de sono** digitam um prompt próprio,
  `>Z..Z..Z...Z.._`, usando exatamente a mesma mecânica do `>THINKING_` (é
  a mesma função, com texto e ritmo como parâmetros). O ritmo é de
  propósito ~2x mais lento que o da IA: na velocidade dela os mesmos
  caracteres passam sensação de ocupado, não de sonolento. Os pontos
  irregulares estão no próprio texto, e não numa temporização variável.
  - **`SLEEPING`** (o sono de 10 min ocioso) põe o prompt na última linha
    da aba IA, igual ao THINKING, com o log rolando acima e o frame inteiro
    já escurecido.
  - **`SLEEPY`** (a hora de dormir) troca a área de conteúdo por um aviso
    de sistema (`SYSTEM NOTICE` / `HUMAN ACTIVITY: LOW`), com a frase em
    PT-BR que o próprio `Personality` sorteia no meio e o prompt na última
    linha. Ela já chega pelo log, então nada extra foi ligado para isso.
    Cromo em inglês, conteúdo em português: é a regra do tema inteiro.
- **`COFFEE`** mantém o cabeçalho, a linha de status e as abas, e cede só a
  área de conteúdo para a xícara — que, como todo o resto, é desenhada em
  âmbar. Mesma ideia do `MATRIX`, que também suprime o log enquanto a
  xícara está na tela e o traz de volta sozinho quando ela sai.
- **Sons são os mesmos dos outros temas** — as cues estilo R2D2 continuam
  tocando aqui. Foi uma decisão explícita de não mexer no `Buzzer`, que hoje
  não recebe o tema; dar beeps próprios ao `MI84` seria um parâmetro `Theme`
  em `playForExpression` e uma tabela `SoundSegment` nova, nada estrutural.

**Sequência de boot.** Ao receber `THEME MI84`, o Core roda uma
inicialização de ~4s antes de mostrar a interface: `MIMO-84 BIOS`, quatro
linhas de POST aparecendo uma a uma (`MEMORY ........ OK`, `DISPLAY`,
`AI CORE`, `AUDIO`), `SYSTEM READY`, e então o letreiro `MIMO SYSTEM v2.6`
centralizado. Terminado isso, os olhos "acendem" como uma lâmpada velha —
apagados, duas partidas falhas que acendem e morrem, e daí subindo até o
brilho cheio.

Ela roda a **cada** comando `THEME MI84`, não uma vez por boot da placa, e
isso é de propósito: o Core não tem nenhum sinal próprio de "um app do PC
acabou de conectar", mas o Brobot.Sender manda o `THEME` como primeira coisa
num link novo e o remanda a cada reconexão. Ou seja, o comando *é* esse
sinal, e a sequência acaba tocando exatamente quando o MiMo volta a ter
contato com o PC.

**Efeito CRT.** O tema não traz nenhum sistema de CRT próprio — usa o que já
existe (`SCANLINES`, no display físico). O tint de fósforo quente do
pós-processamento empurra justamente para o lado do âmbar, e o escurecimento
das scanlines é de propósito fraco, o que importa mais aqui do que em
qualquer outro tema, já que este é quase todo texto de 7px.

## Notificações

Notificação é o topo da escala de prioridade do display:

```
Notificação  >  IA  >  Jogos  >  Mídia
```

São as coisas pelas quais o MiMo **interrompe** você: os lembretes de pausa
(card Pausa), os alertas de mudança do clima (card Clima) e os avisos de
hora de dormir. Diferente de todo o resto, uma notificação **toma a tela
inteira** — sem olhos, sem selos, sem log, sem balão — mostra uma animação
dedicada mais o texto, e **expira sozinha depois de 10s**.

Nada precisa ser restaurado quando ela sai: nenhum tier abaixo é tocado para
abrir espaço, então o frame seguinte simplesmente desenha o que já estava
lá. Se a IA estava com `THINKING` na tela, ela volta por conta própria, sem
o app do PC reenviar coisa alguma.

Chega numa **linha só** (`NOTIFY <EXPRESSÃO> <texto>`), e não no par
`FACE`+`MSG` de sempre. É de propósito: sendo a coisa de maior prioridade do
display, ela não pode ser pega meia-aplicada entre dois comandos, nem ter um
`MSG` solto roteado para outro tier pelo `_lastCommandTier`. O aviso de
dormir não passa por aqui — o Core o levanta sozinho, sem app nenhum
envolvido.

A tela **segue a paleta do tema ativo**: teal no `DEFAULT`, verde no
`MATRIX`, âmbar no `MI84`, e azul-marinho sobre a chapa clara no `MI2MO2` —
esse é o único tema cujo fundo não é preto, e cair para preto ali leria como
a tela ter desligado, não como o MiMo falando.

**O rosto do MiMo aparece em todas as notificações.** Uma versão anterior
dava o frame inteiro para a arte, e quem não tinha ilustração própria caía
num card genérico — uma moldura vazia que ninguém entendia, porque não
representava nada. Fazer do rosto a constante e da arte o opcional eliminou
a necessidade de placeholder.

- **`COFFEE`** (Pausa): olhos à esquerda e **acima** da linha de repouso da
  xícara, que fica à direita, com a mensagem embaixo. A cada ~2,8s a xícara
  **sobe e se aproxima do rosto**, fica um instante lá em cima e desce de
  volta ao pires — o MiMo tomando um gole. É só deslocamento, sem rotação:
  nesta resolução uma caneca inclinada composta de retângulos lê como caneca
  quebrada, não como caneca tombada, então quem carrega o gesto é o
  trajeto. A xícara é a mesma função de desenho do ícone de canto, com a
  geometria parametrizada, e não uma cópia.
- **`SLEEPY`** (aviso de dormir): aqui os olhos *são* a animação, e por isso
  esta notificação não precisa de ícone. Ciclo de 3,6s, que roda ~2x dentro
  dos 10s: as pálpebras caem devagar como quem cochila (curva quadrática —
  quase paradas no começo, cedendo depois), ficam um instante quase
  fechadas, **abrem de susto** passando do tamanho normal, e então piscam
  três vezes rápido antes de recomeçar. Medida a partir de
  `notificationStartedMs`, e não de `nowMs` solto, para o ciclo sempre
  começar acordado — de um relógio livre ele poderia abrir no meio da queda,
  o que lê como falha e não como sono.
- **Qualquer outra**: o rosto centralizado, piscando no ritmo normal do
  `Personality`, com a mensagem embaixo.

Toda arte de notificação recebe a **cor de fundo por parâmetro** em vez de
assumir preto. Isso não é estilo: o truque de "recortar um buraco" usado no
projeto inteiro pinta o recorte da cor do fundo, e no `MI2MO2` o fundo é a
chapa clara — assumindo preto, a caneca ganhava dois buracos pretos e cada
olho, quatro pontinhos escuros nos cantos. Foram dois bugs reais, corrigidos
uma vez.

`FACE`/`MSG` têm duas prioridades independentes, decididas pelo Core (nunca
pelo app PC que envia o comando):

- **Foreground (prioridade alta)** — `THINKING`, `READING`, `FINISHED`,
  `HAPPY`, `SAD`, `ANGRY`, `SLEEPING`, `SLEEPY`, `COFFEE`, `ERROR`, `NEUTRAL`.
  É o que Pensamentos da IA usa. Sempre que ativo, cobre a tela inteira (rosto +
  mensagem), independente do que estava sendo exibido antes.
- **`SLEEPY`** também é escolhido automaticamente pelo Core (sem nenhum app
  PC precisar mandar `FACE`), a partir das 22h até as 6h — baseado na última
  hora recebida via `TIME` (Core não tem RTC próprio, ver acima). É o estado
  de "com sono" (NEUTRAL com os olhos levemente semicerrados, piscada bem
  mais lenta), diferente do `SLEEPING` (que só entra após 10 min sem nenhum
  comando `FACE`/`MSG`, com os "Z Z Z"). Nesse período o Core também
  sorteia, a cada 30 min, uma de 10 frases convidando a dormir e mostra como
  mensagem — tudo autônomo, nenhum app PC participa disso.
- **`COFFEE`** é um comando normal (não autônomo como `SLEEPY`/`SLEEPING`) —
  quem manda é o card Pausa do Brobot.Sender, nos dois horários configurados
  (manhã/tarde), sempre junto de um `MSG` sorteado entre 10 frases
  convidando a esticar as pernas/tomar um café. Visualmente os olhos ficam
  menores e presos no canto esquerdo da tela, com uma xícara de café
  fumegante no canto direito.
- **Background (prioridade baixa)** — `MUSIC`, `WATCHING`, `PLAYING`. É o
  que Mídia/Jogos usam. Fica "guardado" (rosto + mensagem) enquanto o
  foreground estiver ativo, e volta a aparecer automaticamente assim que o
  foreground libera a tela — sem precisar reenviar `FACE`/`MSG`.
- `FACE NEUTRAL` limpa apenas o foreground (ex.: fim de sessão da IA).
  `FACE IDLE` limpa apenas o background (ex.: mídia parou / jogo fechou).
  Enviar o comando errado para a intenção deixa o outro lado preso: quem
  controla mídia/jogo deve mandar `IDLE` para liberar sua própria expressão
  sticky, nunca `NEUTRAL` (que só afeta o lado da IA).

Exemplos:
```
FACE HAPPY
MSG Ola, mundo!
MSG
WEATHER 23 RAIN
TIME 14:32
```

## Comandos de desenho (Brobot Core → PC, somente quando `vscreen=1`)

O Brobot Core nunca decide o que essas linhas significam visualmente — elas
são apenas chamadas de `IDisplay` serializadas. Quem interpreta e desenha é o
Brobot Virtual Display.

| Comando                                  | Equivale a IDisplay             |
|-------------------------------------------|----------------------------------|
| `CLR r g b`                                | `Clear(color)`                   |
| `PIXEL x y r g b`                          | `DrawPixel(x, y, color)`         |
| `RECT x y w h r g b`                       | `DrawRect(...)`                  |
| `FILLRECT x y w h r g b`                   | `FillRect(...)`                  |
| `RRECT x y w h radius r g b`               | `DrawRoundedRect(...)`           |
| `TEXT x y r g b font texto...`             | `DrawText(texto, x, y, color, font)`. `font` é `LATIN` ou `AUREBESH` (só `THEME MI2MO2` chega a mandar `AUREBESH`, ver acima) — cada display decide por conta própria, caractere a caractere, se de fato tem um glifo Aurebesh pra esse caractere ou se cai de volta pra `LATIN`; resto da linha após o token de fonte é o texto, sem mais parsing |
| `PRESENT`                                  | `Present()` — fim do frame, atualiza a tela |

`DrawBitmap` não tem comando de protocolo ainda — não é usado nesta primeira
versão.

## Notas

- A porta Serial é full-duplex: o Arduino pode receber comandos de controle e
  enviar comandos de desenho ao mesmo tempo na mesma porta, sem conflito.
- O Brobot Core não deve imprimir nada em Serial além destas linhas quando
  `vscreen=1` (nada de `Serial.print` de debug), ou o parser do PC vai
  receber lixo. Se precisar logar algo, prefixe com `#` — o parser do PC
  ignora linhas começando com `#`.
- Baud rate: `115200`.
