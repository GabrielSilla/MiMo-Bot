# Builds the Peemo Sender installer end-to-end:
#   1. dotnet publish Brobot.Sender as a self-contained win-x64 app (so the
#      person who assembled a Brobot doesn't need the .NET 8 runtime
#      installed separately — it ships inside the installer).
#   2. Compiles installer\BrobotSenderSetup.iss with Inno Setup's ISCC.exe
#      into installer\output\PeemoSenderSetup-<version>.exe.
#
# Requires Inno Setup 6 (https://jrsoftware.org/isdl.php) — not part of this
# repo/solution, install it once on the machine that builds the installer.

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerDir = $PSScriptRoot
$publishDir = Join-Path $installerDir "publish"
$csproj = Join-Path $repoRoot "src\Brobot.Sender\Brobot.Sender.csproj"

Write-Host "== Publicando Brobot.Sender (self-contained win-x64) ==" -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish falhou (exit code $LASTEXITCODE)"
}

Write-Host "== Compilando a extensao do Visual Studio (Brobot.VSExtension) ==" -ForegroundColor Cyan
# Not dotnet-published like Brobot.Sender above -- a VSIX isn't a
# self-contained app, it's a package VSIXInstaller.exe copies into VS's own
# extensions folder, so a plain Release build (which is what already
# produces the .vsix via the project's own CreateVsixContainer target -- see
# Brobot.VSExtension.csproj's own comments) is all that's needed here. The
# installer bundles this .vsix and, at install time, conditionally hands it
# to VSIXInstaller.exe only on a machine where Visual Studio is actually
# detected (see BrobotSenderSetup.iss's [Code] section) -- most people
# running this installer won't have VS at all, so this must never be a hard
# requirement to build or run Peemo Sender itself.
$vsExtensionCsproj = Join-Path $repoRoot "src\Brobot.VSExtension\Brobot.VSExtension.csproj"
$vsixStagingDir = Join-Path $installerDir "vsix"
if (Test-Path $vsixStagingDir) {
    Remove-Item $vsixStagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $vsixStagingDir | Out-Null

dotnet build $vsExtensionCsproj -c Release --nologo -v quiet

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build da extensao do Visual Studio falhou (exit code $LASTEXITCODE)"
}

$builtVsix = Join-Path $repoRoot "src\Brobot.VSExtension\bin\Release\net472\Brobot.VSExtension.vsix"
if (-not (Test-Path $builtVsix)) {
    throw "Brobot.VSExtension.vsix nao foi gerado em $builtVsix"
}
Copy-Item $builtVsix (Join-Path $vsixStagingDir "Brobot.VSExtension.vsix") -Force

Write-Host "== Compilando o plugin do IntelliJ (Brobot.IntelliJPlugin) ==" -ForegroundColor Cyan
# Same optional-extra shape as the VSIX above: the installer only hands this
# jar to a detected IntelliJ (see [Code] in the .iss). Unlike the VSIX it is
# NOT a hard build requirement -- Gradle needs a JDK 21 and the IntelliJ
# platform to compile against, and the dev machine's own IntelliJ supplies
# both (its bundled JBR, and -PideaPath so Gradle doesn't download ~1.5GB of
# IDE). No IntelliJ here -> warn and ship the installer without the plugin;
# the .iss entry is skipifsourcedoesntexist, so that stays a valid build.
$intellijStagingDir = Join-Path $installerDir "intellij"
if (Test-Path $intellijStagingDir) {
    Remove-Item $intellijStagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $intellijStagingDir | Out-Null

$ideaHome = @(
    (Get-ChildItem "${env:ProgramFiles}\JetBrains" -Directory -Filter "IntelliJ IDEA*" -ErrorAction SilentlyContinue),
    (Get-ChildItem "${env:LOCALAPPDATA}\Programs" -Directory -Filter "IntelliJ IDEA*" -ErrorAction SilentlyContinue)
) | ForEach-Object { $_ } | Where-Object { Test-Path (Join-Path $_.FullName "jbr\bin\java.exe") } |
    Sort-Object Name -Descending | Select-Object -First 1

if (-not $ideaHome) {
    Write-Warning "IntelliJ IDEA nao encontrado -- instalador sera gerado SEM o plugin do IntelliJ."
} else {
    $pluginDir = Join-Path $repoRoot "src\Brobot.IntelliJPlugin"
    $previousJavaHome = $env:JAVA_HOME
    $env:JAVA_HOME = Join-Path $ideaHome.FullName "jbr"
    try {
        Push-Location $pluginDir
        & .\gradlew.bat buildPlugin "-PideaPath=$($ideaHome.FullName -replace '\\','/')" --no-daemon -q
        if ($LASTEXITCODE -ne 0) {
            throw "gradlew buildPlugin do plugin do IntelliJ falhou (exit code $LASTEXITCODE)"
        }
    } finally {
        Pop-Location
        $env:JAVA_HOME = $previousJavaHome
    }

    # The distribution zip is peemo-intellij-plugin/lib/peemo-intellij-plugin-<ver>.jar.
    # Staged under a version-less name so an upgrade overwrites the previous
    # jar instead of leaving two copies of the plugin in the IDE's plugins dir.
    $pluginZip = Get-ChildItem (Join-Path $pluginDir "build\distributions") -Filter *.zip | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $pluginZip) {
        throw "O zip do plugin do IntelliJ nao foi gerado em $pluginDir\build\distributions"
    }
    $extractDir = Join-Path $intellijStagingDir "_extract"
    Expand-Archive $pluginZip.FullName -DestinationPath $extractDir -Force
    $pluginJar = Get-ChildItem $extractDir -Recurse -Filter "peemo-intellij-plugin-*.jar" | Select-Object -First 1
    if (-not $pluginJar) {
        throw "O jar do plugin nao foi encontrado dentro de $($pluginZip.FullName)"
    }
    Copy-Item $pluginJar.FullName (Join-Path $intellijStagingDir "peemo-intellij-plugin.jar") -Force
    Remove-Item $extractDir -Recurse -Force
}

Write-Host "== Localizando o Inno Setup (ISCC.exe) ==" -ForegroundColor Cyan
$isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($isccCommand) {
    $isccPath = $isccCommand.Source
} else {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        # winget (and other per-user installers) put it here instead, with no
        # admin rights and nothing added to PATH — this is where it landed on
        # this dev machine.
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    )
    $isccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $isccPath) {
    throw "ISCC.exe (Inno Setup 6) nao encontrado. Instale em https://jrsoftware.org/isdl.php e rode este script de novo."
}

Write-Host "== Compilando o instalador ==" -ForegroundColor Cyan
& $isccPath (Join-Path $installerDir "BrobotSenderSetup.iss")

if ($LASTEXITCODE -ne 0) {
    throw "ISCC falhou (exit code $LASTEXITCODE)"
}

Write-Host "== Pronto: installer\output\ ==" -ForegroundColor Green
Get-ChildItem (Join-Path $installerDir "output") -Filter "*.exe" | ForEach-Object {
    Write-Host "  $($_.FullName)"
}
