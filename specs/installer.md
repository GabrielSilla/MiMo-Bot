# Installer (Brobot.Sender / "Peemo Sender")

`installer/BrobotSenderSetup.iss` + `installer/build-installer.ps1` package
Brobot.Sender for whoever assembled a Brobot — a real end-user installer, not
a dev tool, since this app is already the one branded "Peemo" throughout (see
[sender-overview.md](sender-overview.md)). Requires Inno Setup 6
(https://jrsoftware.org/isdl.php, not part of this repo/solution — installs
its own `ISCC.exe` compiler) on whichever machine builds the installer.

```powershell
C:\Projects\MiMo-Bot\installer\build-installer.ps1
# Output: installer\output\PeemoSenderSetup-<version>.exe
```

`build-installer.ps1` looks for `ISCC.exe` on PATH first, then falls back to
checking both Program Files locations *and*
`%LOCALAPPDATA%\Programs\Inno Setup 6` — `winget install
JRSoftware.InnoSetup` (no admin rights needed) installs there instead of
Program Files and doesn't add it to PATH, which is what this machine actually
has; missing that path is why the script initially failed to find it after a
winget install even though Inno Setup itself was fine.

The script `dotnet publish`-es Brobot.Sender as **self-contained win-x64**
(bundles the .NET 8 runtime) so the end user needs nothing pre-installed,
then hands the published folder to `ISCC.exe`. Installer is
**Brazilian-Portuguese-only** (`compiler:Languages\BrazilianPortuguese.isl`)
to match the app's own UI language throughout. The csproj's
`SatelliteResourceLanguages=pt-BR` trims WPF/WinForms' own per-language
satellite resource DLLs (ru, tr, zh-Hans, ...) out of that publish output —
dead weight for an app with no other UI language, and it visibly bloated the
self-contained build (publish folder dropped from ~190MB to ~174MB, the
compiled installer from ~57MB to ~53MB).

`PrivilegesRequired=lowest` + `DefaultDirName={autopf}\...` installs
per-user under `LocalAppData\Programs` with no UAC prompt by default (same
pattern VS Code/Discord use) rather than assuming the end user is a machine
admin — `PrivilegesRequiredOverridesAllowed` still lets a "Run as
administrator" launch install per-machine into Program Files instead.

`AppMutex=Brobot.Sender.SingleInstance` in the .iss matches a real named
`Mutex` `App.xaml.cs` now creates on startup (`_singleInstanceMutex`) — this
is what lets Setup detect a running Peemo Sender and offer to close it before
install/uninstall instead of failing on a locked .exe. The mutex is also
what makes a second launch a well-defined no-op (silently `Shutdown()`s
rather than opening a second tray icon/second `AiThoughtsListener` fighting
over port 5591) now that the installer can offer *two* separate shortcuts
that could both start it (Start Menu, and an optional "start with Windows"
shortcut in `{userstartup}`) — before the installer existed this was only
ever launched by hand, so the collision wasn't a real scenario yet.

**Also silently installs `Brobot.VSExtension`'s `.vsix`** (see Repository
layout and [sender-feature-cards.md](sender-feature-cards.md)) when Visual Studio is present on
the machine, and only then — most people running this installer won't have
Visual Studio at all, so this must never be a hard requirement to build or
install Peemo Sender itself. `build-installer.ps1` builds that project in
Release (`dotnet build`, not `dotnet publish` — a VSIX isn't a
self-contained app) and stages the resulting `.vsix` into
`installer\vsix\` before `ISCC.exe` runs, which bundles it into the
installer's own compressed payload (`[Files] ... DestDir: "{tmp}"`).
Detection is Inno Pascal Script in the `.iss`'s own `[Code]` section
(`DetectVisualStudio`), using `vswhere.exe` — shipped at the same stable
path (`{commonpf32}\Microsoft Visual Studio\Installer\vswhere.exe`) by
every VS2017+ install, and the documented, supported way to find an
edition's install directory without guessing at per-edition/version
registry keys. Queried with `-latest -version "[17.0,19.0)"` — the same
range as the VSIX manifest's own `InstallationTarget`, so Setup doesn't
hand the package to an incompatible edition just to have it fail to
register inside VS later. `InitializeSetup` runs the detection once and
caches the result (`VSDetected`/`VSInstallPath`) for the `[Run]` entry's
`Check: ShouldInstallVsExtension`; that entry calls the detected
`VSIXInstaller.exe` directly with `/quiet` (no `/admin` — matching this
installer's own per-user default) so the install is silent, with no
second wizard popping up inside the first one. `[UninstallRun]` mirrors
this to remove the extension again (`/uninstall:<vsixID> /quiet`), but
re-runs `DetectVisualStudio` fresh (`ShouldUninstallVsExtension`) rather
than trusting a value from install time, since VS could have been added,
removed, or moved in the meantime — confirmed live end-to-end (installed
once, uninstalled the extension by hand, ran the installer again: exit
code 0 and a correctly-registered `.pkgdef`). This is also the one piece
of external integration this installer manages without an in-app
Instalar/Desinstalar button the way Atividade da IA's Claude Code hook
has (see `[UninstallRun]`'s own comment in the .iss for why: it's the one
thing *this installer* put there silently, with no equivalent deliberate
user action to undo it, so removing it again is this installer's job too
— unlike the Claude Code hook, which the uninstaller leaves alone below).

The uninstaller otherwise deliberately leaves `%AppData%\Brobot` (settings,
weather/game caches) and any Claude Code hook entries `ClaudeCodeHookInstaller`
wrote to `%USERPROFILE%\.claude\settings.json` untouched — those are the
user's own data/config, not installed program files; anyone who installed
the hook should click "Desinstalar" on Peemo Sender's own Atividade da IA
card first, same as turning it off normally.

`src/Brobot.Sender/src/peemo.ico` is a multi-resolution icon generated from
the same `peemo-b.png` the runtime tray icon already uses (see
`CreateTrayIcon`), so the taskbar/shortcut/installer icon all match — it
isn't hand-drawn, and there's no build step that regenerates it
automatically, so re-run the generation if `peemo-b.png` ever changes. Wired
in via `<ApplicationIcon>` in the csproj, which bakes it into the .exe
itself; this is separate from and in addition to `MainWindow.xaml`'s own
`Window.Icon` (title bar only, PNG, resolved at WPF startup rather than at
the PE level).

## The MiMo -> Peemo rename (1.3.0)

The project was called **MiMo** until 1.2.0 and renamed to **Peemo** in
1.3.0 (name conflict with an existing project). The `AppId` GUID was kept on
purpose, so 1.3.0 installs as an *upgrade* over MiMo Sender rather than a
second app — which also means it lands in the old install folder
(`UsePreviousAppDir` default); only the folder name stays old, and that's
cosmetic. Everything else that carried the old name is handled:

- `[InstallDelete]` removes the old Start Menu group, the old desktop and
  "start with Windows" shortcuts (otherwise there'd be two autostart
  entries) and the old `mimo-*.ps1` scripts in `{app}`; `UsePreviousGroup=no`
  keeps Setup from reusing the old group name.
- Sender's `LegacyNames` is the one place in code that still knows the old
  names, only to read them: the old `mimo-sender-settings.json` is adopted
  once if the new file doesn't exist yet, old theme keys (`MiMoClassic`...)
  and achievement theme tokens (`MI2MO2`/`MI84`) are translated on load,
  and `ClaudeCodeHookInstaller.MigrateLegacyInstall` (run at startup)
  re-registers a Claude Code hook that still points at the old script names.
- Firmware and Sender must be updated together: discovery's identity reply
  changed from `MIMO` to `PEEMO`, and the WiFi setup network from
  `MiMo-Setup` to `Peemo-Setup`.
