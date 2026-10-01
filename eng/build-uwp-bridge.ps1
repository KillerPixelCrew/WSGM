<#
.SYNOPSIS
Builds the packaged-game overlay bridge and stages it for WSGM.PackagedLaunch.

.DESCRIPTION
The bridge is the one DLL WSGM loads into a native UWP game. Inside an
AppContainer, Steam's named IPC objects resolve to the package's own private
namespace, so the game and Steam create different objects with identical names
and the overlay never draws. The bridge hooks the renderer's object creation and
routes the allowlisted names to a desktop broker in the launcher, which opens
the real object and duplicates the handle in. It also routes the engine's WinRT
gamepad activation through Steam's own hook, which is what makes the controller
work in the game rather than merely be enumerated.

It links MinHook statically for the detours, compiled from the vendored tree in
external\minhook.

The build output is staged into src\WSGM.PackagedLaunch\Native\UwpBridge, which
the project copies beside the executable. The staging directory is generated and
is not committed.

Requires MSVC and CMake. A machine without them cannot build the UWP route; the
packaged Win32 route needs none of this.

.PARAMETER Validate
Fail when the produced DLL does not export the entry points the launcher calls.
Used by build.ps1 before a release build, so a staged bridge is one the launcher
can actually drive.
#>
[CmdletBinding()]
param(
    [switch] $Validate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'src\WSGM.PackagedLaunch\Bridge'
$staging = Join-Path $root 'src\WSGM.PackagedLaunch\Native\UwpBridge'
$buildDirectory = Join-Path $root 'publish\uwp-bridge-build'
$minHook = Join-Path $root 'external\minhook'

foreach ($tool in 'cmake') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool is required to build the overlay bridge. Install the Visual Studio C++ workload and CMake."
    }
}

# The generator names the Visual Studio release, so it follows the newest one with the C++ tools
# rather than a fixed year: a machine with only Visual Studio 2026 has no 2022 generator to use.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found; install Visual Studio with the C++ tools.' }
$vsVersion = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationVersion
$generator = switch (([string]$vsVersion).Split('.')[0]) {
    '17' { 'Visual Studio 17 2022' }
    '18' { 'Visual Studio 18 2026' }
    default { throw "No supported Visual Studio with the C++ tools was found (found '$vsVersion')." }
}
$cache = Join-Path $buildDirectory 'CMakeCache.txt'
if ((Test-Path -LiteralPath $cache) -and -not (Select-String -LiteralPath $cache -SimpleMatch "CMAKE_GENERATOR:INTERNAL=$generator" -Quiet)) {
    # CMake refuses to reconfigure a build directory for a different generator.
    Remove-Item -LiteralPath $buildDirectory -Recurse -Force
}

Write-Host "== Configuring the overlay bridge ($generator) ==" -ForegroundColor Cyan
& cmake -S $source -B $buildDirectory -G $generator -A x64 "-DMINHOOK_SOURCE=$minHook"
if ($LASTEXITCODE -ne 0) { throw "Overlay bridge configure failed ($LASTEXITCODE)." }

Write-Host "== Building the overlay bridge ==" -ForegroundColor Cyan
& cmake --build $buildDirectory --config Release
if ($LASTEXITCODE -ne 0) { throw "Overlay bridge build failed ($LASTEXITCODE)." }

$produced = Join-Path $buildDirectory 'Release\WsgmUwpBridge.dll'
if (-not (Test-Path -LiteralPath $produced)) { throw "The overlay bridge was not produced at $produced." }

if ($Validate) {
    # The launcher calls these by name through a remote thread. A DLL that loads and then has no
    # InitializeBridge fails inside somebody's game rather than here.
    $required = @('InitializeBridge', 'InitializeInputBridge', 'InputBridgeRoutes', 'BridgeInitialOwnerRequested')
    $bytes = [IO.File]::ReadAllBytes($produced)
    $text = [Text.Encoding]::ASCII.GetString($bytes)
    foreach ($export in $required) {
        if ($text -notmatch [regex]::Escape($export)) {
            throw "The overlay bridge does not export $export."
        }
    }

    Write-Host "Overlay bridge exports every entry point the launcher calls." -ForegroundColor Green
}

New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -LiteralPath $produced -Destination $staging -Force

# MinHook is BSD-2-Clause and is linked into the DLL, so its licence ships beside it.
Copy-Item -LiteralPath (Join-Path $minHook 'LICENSE.txt') -Destination (Join-Path $staging 'MinHook-LICENSE.txt') -Force

Write-Host "Overlay bridge staged into $staging" -ForegroundColor Green
