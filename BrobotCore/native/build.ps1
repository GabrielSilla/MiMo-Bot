#Requires -Version 5.1
<#
Builds the native (non-Arduino) BrobotCore executable used for local dev
against Brobot Virtual Display, without needing an Arduino attached.

Not part of the PlatformIO project -- compiles the same shared logic
(Face/Personality/Protocol) plus native/src/*.cpp directly with MSVC's
cl.exe, since this machine has no gcc/MinGW for PlatformIO's `native`
platform to use. See native/README.md.
#>
[CmdletBinding()]
param(
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$nativeDir = $PSScriptRoot
$coreDir = Split-Path $nativeDir -Parent
$buildDir = Join-Path $nativeDir "build"
$outExe = Join-Path $buildDir "brobot_native.exe"

if ($Clean -and (Test-Path $buildDir)) {
    Remove-Item -Recurse -Force $buildDir
}
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

# Load the MSVC toolchain into this process's environment, unless cl.exe is
# already reachable (e.g. running from a "Developer PowerShell for VS").
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe not found. Install Visual Studio with the 'Desktop development with C++' workload, or run this script from a 'Developer PowerShell for VS'."
    }

    $vsPath = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) {
        throw "No Visual Studio installation with the 'Desktop development with C++' workload was found."
    }

    $vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
    if (-not (Test-Path $vcvars)) {
        throw "vcvars64.bat not found under '$vsPath'."
    }

    # vcvars64.bat's own internals shell out to vswhere.exe by bare name; make
    # sure it's reachable so that doesn't print a spurious "not recognized" error.
    $env:PATH = "$(Split-Path $vswhere -Parent);$env:PATH"

    Write-Host "Loading MSVC environment from $vcvars"
    $envLines = cmd /c "`"$vcvars`" && set"
    foreach ($line in $envLines) {
        if ($line -match '^([^=]+)=(.*)$') {
            Set-Item -Path "Env:$($Matches[1])" -Value $Matches[2]
        }
    }
}

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    throw "cl.exe still isn't on PATH after loading the MSVC environment."
}

$sources = @(
    (Join-Path $coreDir "src\Face.cpp"),
    (Join-Path $coreDir "src\Personality.cpp"),
    (Join-Path $coreDir "src\Protocol.cpp"),
    (Join-Path $nativeDir "src\TcpBroadcastStream.cpp"),
    (Join-Path $nativeDir "src\main_native.cpp")
)

$includeFlags = @(
    (Join-Path $coreDir "include"),
    (Join-Path $nativeDir "include")
) | ForEach-Object { "/I`"$_`"" }

Push-Location $buildDir
try {
    $clArgs = @("/nologo", "/EHsc", "/std:c++17", "/W3") + $includeFlags + $sources + @("/Fe:brobot_native.exe", "/link", "ws2_32.lib")
    & cl.exe @clArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed (cl.exe exit code $LASTEXITCODE)"
    }
} finally {
    Pop-Location
}

Write-Host "`nBuilt $outExe"
