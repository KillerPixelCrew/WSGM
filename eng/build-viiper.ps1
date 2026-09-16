<#
.SYNOPSIS
Builds the virtual controller library and stages it for WSGM.

.DESCRIPTION
WSGM's virtual controller targets are created by VIIPER, which presents virtual
USB devices in userspace through usbip-win2's generic signed kernel driver. That
is why WSGM ships no driver of its own and needs no kernel code per controller
type.

The source is the external\viiper submodule. Its gitlink pins the
KillerPixelCrew fork's wsgm branch, which carries the downstream fixes as
commits; external\controller\viiper.md records why each one exists. This script
builds the submodule as it is checked out and stages the shared library into
src\WSGM\Native\Viiper, which WSGM.csproj copies beside the executable. The
staging directory is generated and is not committed.

The library exposes a small C ABI over blittable types, keeping its native
ownership and lifetime rules out of the managed device layer.

.PARAMETER Validate
Also vet the whole module and run the library's own tests for the device WSGM
uses and for the C API before building. Used by build.ps1 before a release build
and by the viiper CI job.

.PARAMETER RequirePinned
Refuse to build unless the submodule is clean and checked out at the commit the
superproject's HEAD records. Used by build.ps1, so a release library always
corresponds to a pushed, pinned commit rather than local edits.
#>
[CmdletBinding()]
param(
    [switch] $Validate,
    [switch] $RequirePinned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "external\viiper"
$staging = Join-Path $root "src\WSGM\Native\Viiper"

if (-not (Test-Path -LiteralPath (Join-Path $source "clib"))) {
    throw "VIIPER source not found in external\viiper. Run: git submodule update --init external/viiper"
}

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    throw "Go toolchain not found. Install it (winget install GoLang.Go) — WSGM builds the virtual controller library from source."
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

if ($RequirePinned) {
    $pinned = git -C $root rev-parse "HEAD:external/viiper"
    if ($LASTEXITCODE -ne 0) { throw "Could not read the VIIPER gitlink recorded at HEAD." }
    $actual = git -C $source rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw "Could not read the external\viiper checkout." }
    if ($actual.Trim() -ne $pinned.Trim()) {
        throw "external\viiper is at $($actual.Trim()) but HEAD pins $($pinned.Trim()). A release build must use the pinned commit."
    }
    $changes = git -C $source status --porcelain
    if ($LASTEXITCODE -ne 0) { throw "Could not read the external\viiper status." }
    if ($changes) {
        throw "external\viiper has uncommitted changes. A release build must match its pinned commit."
    }
}

# Recorded so a staged library can be traced to the toolchain that built it.
Write-Host "Go: $(go version)"
Write-Host "C compiler: $((gcc --version | Select-Object -First 1))"

Push-Location $source
try {
    if ($Validate) {
        # vet compiles every package including its tests, which the library build never does, so a
        # test that drifted from an interface change is caught here rather than never.
        go vet ./...
        if ($LASTEXITCODE -ne 0) { throw "VIIPER go vet failed" }
        go test ./device/steamdeck/... ./clib/...
        if ($LASTEXITCODE -ne 0) { throw "VIIPER Steam Deck device or C API tests failed" }
    }

    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    New-Item -ItemType Directory -Path $staging | Out-Null
    $output = Join-Path $staging "libviiper.dll"
    # The submodule's revision is stamped explicitly: Go's -buildvcs does not follow the
    # submodule's .git file and would record the superproject's commit instead. -trimpath keeps
    # build-machine paths out of the shipped library.
    $revision = (git -C $source rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not read the external\viiper revision." }
    $modified = if (git -C $source status --porcelain) { "true" } else { "false" }
    $stamp = "-X main.buildRevision=$revision -X main.buildModified=$modified"
    Write-Host "Building libviiper from $revision (modified: $modified)"
    go build -buildmode=c-shared -trimpath -buildvcs=false -ldflags $stamp -o $output ./clib
    if ($LASTEXITCODE -ne 0) { throw "Failed to build libviiper" }

    # -trimpath also drops -ldflags from `go version -m`, so the stamp is checked in the binary.
    $image = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($output))
    if (-not $image.Contains($revision)) {
        throw "libviiper does not carry its source revision"
    }

    # The tracked header is what WSGM binds against. It replaces the generated one, but only
    # after checking both export the same functions, so an export change cannot ship a header
    # that describes a different ABI.
    $header = Join-Path $staging "libviiper.h"
    $exports = {
        param([string] $path)
        Select-String -LiteralPath $path -Pattern '^extern\s+.*?\b(viiper_\w+)\s*\(' |
            ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique
    }
    $generated = @(& $exports $header)
    $tracked = @(& $exports (Join-Path $source "libviiper.h"))
    $drift = Compare-Object -ReferenceObject $tracked -DifferenceObject $generated
    if ($generated.Count -eq 0 -or $drift) {
        $detail = ($drift | ForEach-Object {
                "$($_.InputObject) ($(if ($_.SideIndicator -eq '=>') { 'exported, missing from libviiper.h' } else { 'declared, not exported' }))"
            }) -join ", "
        throw "libviiper.h does not match the library's exports: $detail"
    }
    Remove-Item -LiteralPath $header -Force
    Copy-Item -LiteralPath (Join-Path $source "libviiper.h") -Destination $header -Force

    foreach ($notice in @(
            @{ Source = "LICENSE.txt"; Destination = "VIIPER-LICENSE.txt" },
            @{ Source = "NOTICE.md"; Destination = "VIIPER-NOTICE.md" }
        )) {
        $noticeSource = Join-Path $source $notice.Source
        if (-not (Test-Path -LiteralPath $noticeSource)) {
            throw "VIIPER source is missing $($notice.Source)"
        }
        Copy-Item -LiteralPath $noticeSource `
            -Destination (Join-Path $staging $notice.Destination) -Force
    }
}
finally {
    Pop-Location
}

Write-Host "Virtual controller library staged into src\WSGM\Native\Viiper"
