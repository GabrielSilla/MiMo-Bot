; Installer for Brobot.Sender ("MiMo Sender"), built with Inno Setup 6
; (https://jrsoftware.org/isdl.php — not part of this repo, install it
; separately to run ISCC.exe). Compiled via build-installer.ps1 in this
; same folder, which publishes the app first and points ISCC at the
; published output below.
;
; AppMutex matches the named Mutex App.xaml.cs creates for its own
; single-instance check (Brobot.Sender.SingleInstance) — this is what lets
; Setup notice a running MiMo Sender and offer to close it automatically
; before installing/uninstalling, instead of failing on a locked .exe.

#define MyAppName "MiMo Sender"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Brobot"
#define MyAppExeName "Brobot.Sender.exe"
#define MyPublishDir "publish"

[Setup]
AppId={{B7E2B6A0-6C8E-4B0B-9C3A-3E0F6E2E3B45}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppMutex=Brobot.Sender.SingleInstance
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Runs without admin rights by default (installs per-user under
; LocalAppData\Programs, same pattern VS Code/Discord use) so a non-technical
; user isn't blocked by a UAC prompt on a machine where they aren't an admin;
; right-clicking Setup and choosing "Run as administrator" still offers a
; per-machine Program Files install instead, via the dialog override below.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=MiMoSenderSetup-{#MyAppVersion}
SetupIconFile=..\src\Brobot.Sender\src\mimo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na Área de Trabalho"; GroupDescription: "Atalhos adicionais:"; Flags: unchecked
Name: "startupicon"; Description: "Iniciar automaticamente com o Windows"; GroupDescription: "Atalhos adicionais:"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar o {#MyAppName} agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; AppMutex above already offers to close a running instance before
; uninstalling; nothing else to stop (no services, no separate processes).

; Deliberately does not remove %AppData%\Brobot (settings, weather/game
; caches) or the Claude Code hook entries ClaudeCodeHookInstaller wrote to
; %USERPROFILE%\.claude\settings.json — those are the user's own data/config,
; not installed program files, and silently deleting either on a routine
; uninstall would be a surprise. Anyone who installed the Claude Code hook
; should click "Desinstalar" on MiMo Sender's own Atividade da IA card
; before uninstalling the app, same as they would to turn it off normally.
