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
| `THEME <nome>` | Estilo visual do display inteiro. Valores: `DEFAULT` (olhos + balão de mensagem, o padrão) ou `MATRIX` (olhos menores no canto inferior + log estilo console em verde no topo, ver abaixo). Persistente, como `WEATHER`/`TIME` — fica valendo até o próximo `THEME` chegar. |

`WEATHER`, `TIME` e `THEME` são independentes de `FACE`/`MSG`: não interrompem nem são interrompidos por eles, não "expiram" sozinhos, e ficam visíveis/valendo até o próximo comando do mesmo tipo substituí-los.

**`THEME MATRIX`**: recolore tudo em verde e troca os selos de clima/relógio e o balão de mensagem por um log estilo terminal no topo da tela (linhas prefixadas com `> `, mais antigas saem conforme novas entram — até 6 linhas visíveis). Toda mensagem que normalmente apareceria no balão (IA, mídia, jogos, pausa, boa-noite) vira uma linha nova nesse log; clima+hora, quando ambos configurados, também viram uma linha combinada (`HH:MM - tempC - condição`) uma vez por minuto. Puramente visual — os comandos `FACE`/`MSG`/`WEATHER`/`TIME` continuam funcionando exatamente igual por baixo, só a forma de desenhar muda.

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
| `TEXT x y r g b texto...`                  | `DrawText(texto, x, y, color)` (resto da linha é o texto, sem mais parsing) |
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
