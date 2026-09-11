<#
.SYNOPSIS
Builds the virtual controller library and stages it for WSGM.

.DESCRIPTION
WSGM's virtual controller targets are created by VIIPER, which presents virtual
USB devices in userspace through usbip-win2's generic signed kernel driver. That
is why WSGM ships no driver of its own and needs no kernel code per controller
type.

Unlike the Steam Input lease, the source is not vendored into
this repository: it is an external project pinned by revision in
third_party\controller\viiper\README.md. The pin names the KillerPixelCrew fork,
whose wsgm branch carries the downstream fixes as commits, so this script only
checks the revision out, builds the shared library, and stages it into
src\WSGM\Native\Viiper, which WSGM.csproj copies beside the executable. The
staging directory is generated and is not committed.

The library exposes a small C ABI over blittable types, keeping its native
ownership and lifetime rules out of the managed device layer.

.PARAMETER SourceRoot
Directory holding the VIIPER checkout. Defaults to a sibling of the repository
so a normal build does not re-clone on every run.

.PARAMETER Validate
Also run the library's own tests for the device WSGM uses before building. Used
by eng\verify.ps1; the release build skips them because verify.ps1 has already
run them.
#>
[CmdletBinding()]
param(
    [string] $SourceRoot,
    [switch] $Validate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $root "src\WSGM\Native\Viiper"

# Pinned by revision, not by branch tip: a moving branch would silently change
# what ships between two builds of the same WSGM commit. The source is the
# KillerPixelCrew fork's wsgm branch, which carries the downstream patch set as
# reviewable commits on top of the corando98/VIIPER viiper-controller baseline.
$repository = "https://github.com/KillerPixelCrew/VIIPER.git"
$revision = "fe726ce80bd2995a8b149440d977a561850d9e89"

if (-not $SourceRoot) {
    $SourceRoot = Join-Path (Split-Path -Parent $root) "wsgm-viiper"
}

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    throw "Go toolchain not found. Install it (winget install GoLang.Go) — WSGM builds the virtual controller library from source."
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Git not found. It is required to check out the pinned VIIPER revision."
}

# The library exposes a C ABI, so cgo needs a C compiler. Go defaults CGO_ENABLED
# to 0 when it cannot find one, and then reports the far less obvious "build
# constraints exclude all Go files" instead of naming the missing toolchain.
if (-not (Get-Command gcc -ErrorAction SilentlyContinue)) {
    $wingetPackages = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    $candidate = if (Test-Path -LiteralPath $wingetPackages) {
        Get-ChildItem -LiteralPath $wingetPackages -Recurse -Filter "gcc.exe" `
            -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    else {
        $null
    }

    if ($null -eq $candidate) {
        throw "C compiler not found. Install one (winget install BrechtSanders.WinLibs.POSIX.UCRT) — cgo needs it to build the virtual controller library."
    }

    $env:Path = "$($candidate.DirectoryName);$env:Path"
}

$env:CGO_ENABLED = "1"

if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot ".git"))) {
    Write-Host "Cloning pinned VIIPER revision into $SourceRoot"
    New-Item -ItemType Directory -Force -Path $SourceRoot | Out-Null
    git clone --quiet $repository $SourceRoot
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone VIIPER from $repository" }
}

Push-Location $SourceRoot
try {
    # An existing checkout was cloned from whichever repository the pin named at
    # the time. Point origin at the current one before fetching, or moving the
    # pin to a different fork fails as "revision unavailable" on every machine
    # that already has a source tree.
    git remote set-url origin $repository
    if ($LASTEXITCODE -ne 0) { throw "Failed to point $SourceRoot at $repository" }

    # Reset hard so a repeated build is idempotent, and so a checkout left dirty
    # by an interrupted run does not silently ship modified source.
    git fetch --quiet origin $revision 2>$null | Out-Null
    git checkout --quiet --force $revision
    if ($LASTEXITCODE -ne 0) { throw "Pinned VIIPER revision $revision is unavailable" }
    git reset --quiet --hard $revision
    git clean -qfd

    if ($Validate) {
        go test ./device/steamdeck/...
        if ($LASTEXITCODE -ne 0) { throw "VIIPER Steam Deck device tests failed" }
    }

    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    New-Item -ItemType Directory -Path $staging | Out-Null
    $output = Join-Path $staging "libviiper.dll"
    Write-Host "Building libviiper"
    go build -buildmode=c-shared -o $output ./clib
    if ($LASTEXITCODE -ne 0) { throw "Failed to build libviiper" }

    # The generated header is staged next to the library so the ABI WSGM binds
    # against is inspectable beside the binary it came from.
    $header = Join-Path $staging "libviiper.h"
    if (Test-Path -LiteralPath $header) { Remove-Item -LiteralPath $header -Force }
    Copy-Item -LiteralPath (Join-Path $SourceRoot "libviiper.h") -Destination $header -Force

    foreach ($notice in @(
            @{ Source = "LICENSE.txt"; Destination = "VIIPER-LICENSE.txt" },
            @{ Source = "NOTICE.md"; Destination = "VIIPER-NOTICE.md" }
        )) {
        $source = Join-Path $SourceRoot $notice.Source
        if (-not (Test-Path -LiteralPath $source)) {
            throw "Pinned VIIPER source is missing $($notice.Source)"
        }
        Copy-Item -LiteralPath $source `
            -Destination (Join-Path $staging $notice.Destination) -Force
    }
}
finally {
    Pop-Location
}

Write-Host "Virtual controller library staged into src\WSGM\Native\Viiper"
