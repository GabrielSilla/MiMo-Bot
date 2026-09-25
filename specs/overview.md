# Brobot

A desktop buddy robot. Brobot Core is one independent project that never
links against anything on the PC side — it only talks a text-line serial
protocol. On the PC side, Core is always "the device": any number of
independent Windows apps can each connect to it directly, the same way more
than one app could each open a COM port to talk to an Arduino at different
times (real COM ports only ever have one owner at a time; Core's native/TCP
build additionally accepts several simultaneous connections, purely as a
dev convenience the real serial link can't offer — see native/README.md).

```
                                    <--serial (COM) or TCP-->  Brobot Virtual Display (watches)
Brobot Core (Arduino/native, C++)
                                    <--WiFi TCP-->               Brobot Sender (sends FACE/MSG/WEATHER/TIME)
   personality + animation              both are dumb clients, no Brobot logic
```

Sender only ever reaches Core over WiFi TCP now (see BrobotConnection internals below
for why Serial got dropped from that side); Virtual Display still supports both.

## The one rule that matters

**PC-side apps render or send. Brobot Core decides.** Neither Brobot Virtual
Display nor Brobot Sender may ever contain Brobot states, emotions,
blink/look logic, or hardcoded messages. Virtual Display only implements
`IDisplay` (Clear/DrawPixel/DrawRect/FillRect/DrawRoundedRect/DrawText/
DrawBitmap) and renders whatever it's told; Sender only turns a checked
checkbox (or, behind the scenes, a Windows media-session event, a weather
API response, the system clock) into `FACE`/`MSG`/`WEATHER`/`TIME` command
lines — it never decides what a state *means*, e.g. it doesn't pick which
expression "rain" maps to, Core does. All personality (blinking, looking
around, sleeping, expressions, message typing/timing) lives in Brobot Core.
This split is intentional so that a future `ST7735Display` (real hardware)
can drop in next to `SimulatorDisplay` without either one knowing the other
exists, and so any number of PC-side tools can be added later without ever
needing to know Brobot's actual behavior.

## Repository layout

```
BrobotVirtualDisplay.slnx           .NET solution (open with `dotnet build`)
src/
  Brobot.Display.Abstractions/      IDisplay + DisplayColor — the shared contract
  Brobot.Display.Simulator/         WPF app: SimulatorDisplay, MainWindow, SerialDisplayBridge, Font5x7,
                                     AurebeshFont (THEME P2M2's alien script, see specs/firmware-face-themes.md)
  Brobot.Connection/                C#: BrobotConnection — shared COM/TCP client used by both
                                     Simulator and Sender to reach Core — plus PeemoDiscovery, which
                                     finds Peemo's DHCP-assigned IP on the network (see specs/architecture.md)
  Brobot.Sender/                    WPF tray app, branded "Peemo" to the user, for whoever assembled a
                                     Brobot: MainWindow is three tabs (Configurações Gerais/Mini Games/
                                     Conquistas — see specs/sender-overview.md). Configurações Gerais is the original
                                     card-based checklist (Conexão, Hora, Clima, Pausa, Relatório,
                                     Notificações, Atividade da IA, Mídia, Jogos, Build, Tema, Sons,
                                     Scanlines — mostly checkboxes, except Conexão (a status readout plus
                                     one button), Tema (a ComboBox) and Atividade da IA (an
                                     Instalar/Desinstalar button), see specs/sender-overview.md). Mini Games holds one card per minigame (Pong, Batalha
                                     RPG — see BrobotCore below and specs/sender-minigames-achievements.md), each
                                     with its own JOGAR/BATALHAR button. Conquistas holds 10 fixed
                                     achievements (AchievementCatalog), tracked by AchievementMonitor
                                     against signals the other monitors below already raise — unlocking
                                     one also flashes a NOTIFY on Peemo's own screen (see
                                     specs/sender-minigames-achievements.md). The Conexão card is a readout, not a setup form —
                                     Peemo's address is discovered, never typed (WiFi/TCP only — no
                                     SettingsWindow, no Serial/USB, see specs/sender-overview.md). WeatherMonitor +
                                     WindowsMediaMonitor + GameMonitor + NotificationMonitor +
                                     AiThoughtsListener are the live data sources so far (Notificações also
                                     runs TeamsNotificationWatcher alongside NotificationMonitor — see
                                     specs/sender-monitors.md for why Teams needs its own, much more
                                     fragile capture path), Ferramentas de Dev (still internally
                                     BuildCheckBox/BuildEnabled — see specs/sender-feature-cards.md)
                                     runs GradleBuildLogMonitor and MsBuildProcessMonitor and also
                                     installs a global git hook (GitHookInstaller, reporting over
                                     AiThoughtsListener same as the AI bridge below — see
                                     specs/sender-feature-cards.md for why Visual Studio's own builds
                                     still need a completely separate mechanism, Brobot.VSExtension,
                                     not a monitor in this process at all),
                                     ClaudeCodeHookInstaller edits
                                     Claude Code's own settings.json, ClaudeCodeAccount reads (never writes)
                                     ~/.claude.json to detect an account switch, SenderSettings persists
                                     checkbox/provider/connection state to %AppData%, GlobalKeyboardHook,
                                     GlobalHotkey and TeamsNotificationWatcher are this app's P/Invoke —
                                     GlobalKeyboardHook is a system-wide low-level keyboard hook the two
                                     minigames use to read arrow/Enter/Escape regardless of window focus,
                                     GlobalHotkey is RegisterHotKey/WM_HOTKEY instead (CTRL+SHIFT+F10 shows
                                     the partial Relatório from anywhere, see specs/sender-feature-cards.md
                                     — plain ALT+F10 was tried first and dropped once RegisterHotKey
                                     confirmed something else on a real dev machine already owns it).
                                     Icons via the MahApps.Metro.IconPacks.Material NuGet package.
  Brobot.VSExtension/                A real Visual Studio extension (VSIX, `net472` — devenv.exe is
                                     still .NET Framework even in modern VS), separate from
                                     Brobot.Sender's own process entirely: `BrobotBuildWatcherPackage`
                                     (an `AsyncPackage`) hooks `EnvDTE.BuildEvents.OnBuildProjConfigBegin/
                                     Done` in-process and reports over the same AiThoughtsListener TCP
                                     wire hooks/peemo-claude-hook.ps1 uses (`VsBuildStarted`/
                                     `VsBuildSucceeded`/`VsBuildFailed`, one line each, port 5591) — see
                                     specs/sender-feature-cards.md for why this exists as a separate
                                     project instead of a fourth monitor next to Gradle/MSBuild's. Kept
                                     out of BrobotVirtualDisplay.slnx (separate build, same treatment as
                                     BrobotCore/native). Bundled into the Peemo Sender installer (see
                                     specs/installer.md) and installed automatically when Visual Studio is
                                     detected on the machine — there's no in-app Instalar/Desinstalar
                                     button for it the way Atividade da IA's Claude Code hook has, since
                                     there's no equivalent "is VS even here" question to ask from inside
                                     the app before the installer already answered it.
hooks/                               peemo-claude-hook.ps1 (the Claude Code hook command) and
                                     peemo-claude-statusline.ps1 (its statusLine command — a different
                                     contract, see specs/sender-ai-bridge.md); both wired up by ClaudeCodeHookInstaller and
                                     copied to Brobot.Sender's build output (see its csproj) rather
                                     than run from here. peemo-git-hook.ps1 and git-hooks/
                                     (post-commit/post-merge/post-checkout/pre-push, static shims
                                     pinned to LF via .gitattributes) are the git-side equivalent,
                                     wired up by GitHookInstaller — see specs/sender-feature-cards.md.
BrobotCore/                         PlatformIO project (Arduino/C++)
  include/, src/                    Config, IDisplay, SerialVirtualDisplay, ST7735PhysicalDisplay,
                                     WifiSetup (ESP32 only — WiFi provisioning, see specs/firmware-platform.md),
                                     Buzzer (R2D2-style beeps, see specs/firmware-platform.md — firmware-only, not part
                                     of the native build's shared source list), DeviceSettings
                                     (the SOUND/SCANLINES toggles, header-only),
                                     AurebeshGFXFont.h (THEME P2M2's alien script as an Adafruit
                                     GFXfont — the firmware-side twin of AurebeshFont.cs),
                                     Face, Personality, Protocol, main.cpp, PongGame, RpgBattle
                                     (Peemo's two exclusive minigames, launched from Brobot.Sender's
                                     Mini Games tab — see PROTOCOL.md's Pong/Batalha RPG sections
                                     and specs/firmware-platform.md)
  platformio.ini                    envs: uno, uno_physical, esp32dev, esp32dev_physical
                                     (esp32* envs use board=esp32-c3-devkitm-1 — the actual
                                     hardware in hand is an ESP32-C3 SuperMini clone board)
  native/                           Dev-only build of Face/Personality/Protocol as a Windows .exe —
                                     no Arduino required. Listens on TCP the same way a real device's
                                     COM port is "the thing PC apps connect to". See native/README.md.
                                     Not part of the PlatformIO project; built separately with MSVC.
PROTOCOL.md                         Single source of truth for the serial wire protocol
```
