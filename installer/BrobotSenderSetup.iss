; Installer for Brobot.Sender ("Peemo Sender"), built with Inno Setup 6
; (https://jrsoftware.org/isdl.php — not part of this repo, install it
; separately to run ISCC.exe). Compiled via build-installer.ps1 in this
; same folder, which publishes the app first and points ISCC at the
; published output below.
;
; AppMutex matches the named Mutex App.xaml.cs creates for its own
; single-instance check (Brobot.Sender.SingleInstance) — this is what lets
; Setup notice a running Peemo Sender and offer to close it automatically
; before installing/uninstalling, instead of failing on a locked .exe.

#define MyAppName "Peemo Sender"
#define MyAppVersion "1.5.0"
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
; Otherwise an upgrade from MiMo Sender would keep reusing the old
; "MiMo Sender" Start Menu group name (see [InstallDelete]).
UsePreviousGroup=no
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
OutputBaseFilename=PeemoSenderSetup-{#MyAppVersion}
SetupIconFile=..\src\Brobot.Sender\src\peemo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na Área de Trabalho"; GroupDescription: "Atalhos adicionais:"; Flags: unchecked
; Checked by default: Peemo Sender is a tray app that only does its job while
; running. On an upgrade Inno still reuses the previous choice
; (UsePreviousTasks), so someone who unticked it before keeps it off.
Name: "startupicon"; Description: "Iniciar automaticamente com o Windows"; GroupDescription: "Atalhos adicionais:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Small file (a few KB) -- always extracted to {tmp}, not conditioned on
; DetectVisualStudio finding anything, since checking that here would need
; the same vswhere.exe Exec call twice (once for Files, once for [Run]) for
; no real savings. What actually gates the install is the [Run] entry below.
Source: "vsix\Brobot.VSExtension.vsix"; DestDir: "{tmp}"
; The IntelliJ plugin's jar: also always extracted to {tmp}; [Code]'s
; CurStepChanged copies it into each detected IntelliJ config directory.
; skipifsourcedoesntexist because build-installer.ps1 only produces it on a
; machine that has IntelliJ to compile against.
Source: "intellij\peemo-intellij-plugin.jar"; DestDir: "{tmp}"; Flags: skipifsourcedoesntexist

; Upgrade from before the MiMo -> Peemo rename: same AppId, so Setup
; installs over the old copy (into its existing folder), but the shortcuts
; and hook scripts carried the old name and would otherwise linger next to
; the new ones — two "start with Windows" entries, a stale Start Menu group.
; The app itself re-points an installed Claude Code hook at the renamed
; scripts on startup (ClaudeCodeHookInstaller.MigrateLegacyInstall), so
; deleting the old scripts here doesn't break it.
[InstallDelete]
Type: files; Name: "{userstartup}\MiMo Sender.lnk"
Type: files; Name: "{autodesktop}\MiMo Sender.lnk"
Type: filesandordirs; Name: "{autoprograms}\MiMo Sender"
Type: files; Name: "{app}\mimo-claude-hook.ps1"
Type: files; Name: "{app}\mimo-claude-statusline.ps1"
Type: files; Name: "{app}\mimo-git-hook.ps1"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
; The VS extension is NOT installed from here but from [Code]'s
; InstallVsExtension: a [Run] entry can't see VSIXInstaller's exit code, and
; with VS open it fails silently after about a minute of retrying
; (BlockingProcessesException), which is exactly how it went unnoticed.
; Deliberately unlike Atividade da IA's Claude Code hook, which is only ever
; installed via an explicit, reversible Instalar/Desinstalar button inside
; the app itself -- the app has no UI to show/hide before VS is even known to
; be present. If this ever needs to be user-toggleable the same way, that
; button belongs in the app, not here.
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar o {#MyAppName} agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; AppMutex above already offers to close a running instance before
; uninstalling; nothing else to stop (no services, no separate processes).

; The VS extension is the one exception to the "leave what the app itself
; manages alone" policy described below -- it's the one piece of external
; state this *installer* put there silently, with no button in the app and
; no explicit user action the way the Claude Code hook has, so removing it
; is this installer's own responsibility to undo, not something to leave
; for the user to hunt down. ShouldUninstallVsExtension re-runs the same
; vswhere.exe detection Setup used (see [Code] below) since the VS install
; found at install time may no longer be the one present now.
Filename: "{code:GetVsixInstallerPath}"; Parameters: "/uninstall:872793e5-5485-4c40-b6ab-d655b897827c /quiet"; Check: ShouldUninstallVsExtension; RunOnceId: "UninstallBrobotVsExtension"; Flags: runhidden waituntilterminated

; Deliberately does not remove %AppData%\Brobot (settings, weather/game
; caches) or the Claude Code hook entries ClaudeCodeHookInstaller wrote to
; %USERPROFILE%\.claude\settings.json — those are the user's own data/config,
; not installed program files, and silently deleting either on a routine
; uninstall would be a surprise. Anyone who installed the Claude Code hook
; should click "Desinstalar" on Peemo Sender's own Atividade da IA card
; before uninstalling the app, same as they would to turn it off normally.

[Code]
var
  VSInstallPath: String;
  VSDetected: Boolean;
  // Set when the user chose to go on without the VS extension because
  // Visual Studio was still open (see PrepareToInstall).
  VsExtensionSkipped: Boolean;

// vswhere.exe has shipped at this exact, stable path alongside the Visual
// Studio Installer since VS2017 -- it's the documented, supported way to
// locate any installed VS edition (Community/Professional/Enterprise) and
// its install directory without guessing at registry keys that differ per
// edition/version. {commonpf32} (not {commonpf}) because the VS Installer,
// and therefore vswhere.exe, is always a 32-bit tool even on a 64-bit OS.
function VSWhereExePath(): String;
begin
  Result := ExpandConstant('{commonpf32}') + '\Microsoft Visual Studio\Installer\vswhere.exe';
end;

// Finds the newest VS install matching this extension's own manifest range
// (source.extension.vsixmanifest's InstallationTarget Version="[17.0,19.0)")
// -- installing into an incompatible edition would just fail later inside
// VS, so there's no point handing it to VSIXInstaller at all if none
// qualifies. Sets VSInstallPath as a side effect for GetVsixInstallerPath
// to read; returns False (leaving VSInstallPath empty) on any machine with
// no VS Installer at all, which is the common case for this installer.
function DetectVisualStudio(): Boolean;
var
  VsWhere, OutputFile, CmdLine: String;
  ResultCode: Integer;
  Lines: TArrayOfString;
begin
  Result := False;
  VSInstallPath := '';

  VsWhere := VSWhereExePath();
  if not FileExists(VsWhere) then
    Exit;

  OutputFile := ExpandConstant('{tmp}\brobot-vswhere-output.txt');
  // -prerelease: without it vswhere skips Insiders/Preview channels entirely,
  // and Visual Studio 2026 (18.x) Insiders is a real target of this extension.
  CmdLine := '/C ""' + VsWhere + '" -products * -latest -prerelease -version "[17.0,19.0)" -property installationPath > "' + OutputFile + '""';
  if not Exec(ExpandConstant('{cmd}'), CmdLine, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;

  if not (LoadStringsFromFile(OutputFile, Lines) and (GetArrayLength(Lines) > 0)) then
    Exit;

  VSInstallPath := Trim(Lines[0]);
  Result := (VSInstallPath <> '') and DirExists(VSInstallPath);
end;

function ShouldUninstallVsExtension(): Boolean;
begin
  // Re-detected rather than reusing a value from install time -- this is a
  // separate process (the uninstaller), possibly run long after install,
  // and Visual Studio could have been added, removed, or moved since.
  Result := DetectVisualStudio();
end;

function GetVsixInstallerPath(Param: String): String;
begin
  Result := VSInstallPath + '\Common7\IDE\VSIXInstaller.exe';
end;

// VSIXInstaller refuses to change extensions while devenv.exe runs. Matched
// by name only: WMI can't read ExecutablePath of an elevated devenv from a
// non-elevated Setup, and any open VS is worth asking about anyway.
function IsVisualStudioRunning(): Boolean;
var
  Locator, Service, Procs: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''devenv.exe''');
    Result := Procs.Count > 0;
  except
    // WMI unavailable: don't block Setup; InstallVsExtension still reports
    // a failure if VS really was open.
    Result := False;
  end;
end;

// Asks for VS to be closed *before* anything is installed, while the user can
// still act on it. Cancel just skips the extension; Peemo Sender itself is
// never blocked on Visual Studio. In a silent install the default (Cancel)
// applies, so an unattended install never hangs on an open VS.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not VSDetected then
    Exit;
  while IsVisualStudioRunning() do
  begin
    if SuppressibleMsgBox('O Visual Studio está aberto. Para instalar a extensão do Peemo para o Visual Studio, feche-o e clique em Repetir.' + #13#10#13#10 +
        'Clique em Cancelar para continuar sem a extensão. O Peemo Sender é instalado normalmente.',
        mbConfirmation, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      VsExtensionSkipped := True;
      Exit;
    end;
  end;
end;

// Runs VSIXInstaller and checks its exit code, unlike a [Run] entry. Even
// with devenv.exe closed it can still be blocked by MSBuild.exe worker nodes
// that VS leaves running for a few minutes, so a failure offers Retry.
// "This exact version is already installed" (a reinstall, or an upgrade that
// didn't change the VSIX) is success here. It is recognized by the exception
// name VSIXInstaller writes to its log, not by exit code: there is no
// documented table of its exit codes.
function VsixLogSaysAlreadyInstalled(LogFile: String): Boolean;
var
  Log: AnsiString;
begin
  Result := LoadStringFromFile(LogFile, Log) and (Pos('AlreadyInstalledException', Log) > 0);
end;

procedure InstallVsExtension();
var
  Params, LogFile: String;
  ResultCode: Integer;
begin
  if (not VSDetected) or VsExtensionSkipped then
    Exit;
  LogFile := ExpandConstant('{%TEMP}\PeemoSender-vsix-install.log');
  Params := '/quiet "/logFile:' + LogFile + '" "' + ExpandConstant('{tmp}\Brobot.VSExtension.vsix') + '"';
  repeat
    WizardForm.StatusLabel.Caption := 'Instalando a extensão do Peemo para o Visual Studio...';
    DeleteFile(LogFile);
    if Exec(GetVsixInstallerPath(''), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and
       ((ResultCode = 0) or VsixLogSaysAlreadyInstalled(LogFile)) then
      Exit;
  until SuppressibleMsgBox('Não foi possível instalar a extensão do Peemo para o Visual Studio (código ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
      'Normalmente é algum processo do Visual Studio ainda aberto, como o MSBuild.exe, que continua ativo por alguns minutos depois que o VS fecha. ' +
      'Feche-os no Gerenciador de Tarefas e clique em Repetir, ou clique em Cancelar para seguir sem a extensão.' + #13#10#13#10 +
      'Detalhes em: ' + LogFile,
      mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY;
end;

// IntelliJ plugin. Unlike Visual Studio there's no CLI installer to hand a
// package to -- a user plugin is just a folder in the IDE's *config*
// directory, %APPDATA%\JetBrains\<product><version>\plugins\<name>\lib\*.jar,
// picked up the next time that IDE starts. "Is IntelliJ installed" is
// therefore answered the same way the IDE itself answers it: by which config
// directories exist, one per major version the user has ever launched. The
// plugin is built for 2025.3+ (sinceBuild 253 in build.gradle.kts), so older
// config directories are left alone -- the IDE would refuse the plugin there.
// Community used to be a separate product ("IdeaIC"); both prefixes are
// matched in case an older-named directory is still around.
const
  IntelliJPluginMinVersion = '2025.3';
  IntelliJPluginFolder = 'peemo-intellij-plugin';

// Calls back once per supported IntelliJ config directory. Install = True
// copies the jar into each; False removes the plugin folder from each.
procedure ForEachIntelliJConfigDir(Install: Boolean);
var
  Root, Name, Version, PluginDir, Jar: String;
  FindRec: TFindRec;
  Prefix: Integer;
begin
  Root := ExpandConstant('{userappdata}') + '\JetBrains\';
  if not FindFirst(Root + '*', FindRec) then
    Exit;
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Name := FindRec.Name;
        Prefix := 0;
        if Pos('IntelliJIdea', Name) = 1 then
          Prefix := Length('IntelliJIdea')
        else if Pos('IdeaIC', Name) = 1 then
          Prefix := Length('IdeaIC');

        if Prefix > 0 then
        begin
          Version := Copy(Name, Prefix + 1, Length(Name));
          // "2025.3" style: a plain string compare orders these correctly
          // (four-digit year, then the release number).
          if CompareStr(Version, IntelliJPluginMinVersion) >= 0 then
          begin
            PluginDir := Root + Name + '\plugins\' + IntelliJPluginFolder;
            if Install then
            begin
              Jar := ExpandConstant('{tmp}\peemo-intellij-plugin.jar');
              if FileExists(Jar) and ForceDirectories(PluginDir + '\lib') then
                CopyFile(Jar, PluginDir + '\lib\peemo-intellij-plugin.jar', False);
            end
            else
              DelTree(PluginDir, True, True, True);
          end;
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ForEachIntelliJConfigDir(True);
    InstallVsExtension();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Same reasoning as the VS extension's [UninstallRun] above: this
  // installer put the plugin there silently, so removing it is its job too.
  if CurUninstallStep = usUninstall then
    ForEachIntelliJConfigDir(False);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  VSDetected := DetectVisualStudio();
end;
