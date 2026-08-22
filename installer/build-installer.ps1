# Builds the MiMo Sender installer end-to-end:
#   1. dotnet publish Brobot.Sender as a self-contained win-x64 app (so the
#      person who assembled a Brobot doesn't need the .NET 8 runtime
#      installed separately — it ships inside the installer).
#   2. Compiles installer\BrobotSenderSetup.iss with Inno Setup's ISCC.exe
#      into installer\output\MiMoSenderSetup-<version>.exe.
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
