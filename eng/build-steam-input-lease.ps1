<#
.SYNOPSIS
Builds the Steam Input Lease library and stages its output for WSGM.

.DESCRIPTION
The library is the external\steam-input-lease submodule and is built from
source on every WSGM build, so the shipped gate can never drift from the code
next to it. Its build output is staged into src\WSGM\Native\SteamInputLease,
which WSGM.csproj copies beside the application executable and the installer ships.
That staging directory is generated and is not committed.

.PARAMETER Validate
Also run the library's own gates (clippy as errors, then the unit tests) before
building. Both eng\verify.ps1 and build.ps1 pass this switch.
#>
[CmdletBinding()]
param(
    [switch]$Validate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'build-common.ps1')
$root = Split-Path -Parent $PSScriptRoot
$library = Join-Path $root "external\steam-input-lease"
$manifest = Join-Path $library "Cargo.toml"
$staging = Join-Path $root "src\WSGM\Native\SteamInputLease"

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "Rust toolchain not found. Install it from https://rustup.rs; WSGM builds external\steam-input-lease from source."
}

if ($Validate) {
    cargo clippy --manifest-path $manifest --workspace --all-targets -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw "Steam Input Lease clippy check failed" }

    cargo test --manifest-path $manifest --workspace
    if ($LASTEXITCODE -ne 0) { throw "Steam Input Lease tests failed" }
}

cargo build --manifest-path $manifest --workspace --release
if ($LASTEXITCODE -ne 0) { throw "Steam Input Lease release build failed" }

$release = if ($env:CARGO_BUILD_TARGET) {
    Join-Path $library "target\$($env:CARGO_BUILD_TARGET)\release"
} else {
    Join-Path $library "target\release"
}

if ($Validate) {
    # The same DLL serves as an XInput proxy and the name-resolved DirectInput
    # fallback. Its explicit export map is load-bearing: rustc's automatic
    # ordinals once placed DirectInput8Create and DllRegisterServer at XInput's
    # undocumented 104 and 109 slots, so a dynamic ordinal lookup could call an
    # incompatible signature during Steam startup.
    $gate = Join-Path $release "steam_input_gate.dll"
    $exports = @(Get-DllExports -Path $gate)

    $expectedNamedExports = [ordered]@{
        DllMain = 1
        XInputGetState = 2
        XInputSetState = 3
        XInputGetCapabilities = 4
        XInputEnable = 5
        XInputGetBatteryInformation = 7
        XInputGetKeystroke = 8
        XInputGetAudioDeviceIds = 10
        DirectInput8Create = 200
        DllCanUnloadNow = 201
        DllGetClassObject = 202
        DllRegisterServer = 203
        DllUnregisterServer = 204
        GetdfDIJoystick = 205
        WsgmSteamInputGateProxy = 206
    }
    foreach ($entry in $expectedNamedExports.GetEnumerator()) {
        $ordinal = [int]$entry.Value
        if (-not ($exports | Where-Object { $_.Ordinal -eq $ordinal -and $_.Name -ceq $entry.Key })) {
            throw "Steam Input gate export $($entry.Key) is not at ordinal $ordinal"
        }
    }
    foreach ($ordinal in @(100, 101, 102, 103, 108)) {
        if (-not ($exports | Where-Object { $_.Ordinal -eq $ordinal -and $_.Name -ceq '[NONAME]' })) {
            throw "Steam Input gate is missing ordinal-only XInput export $ordinal"
        }
    }
    if ($exports | Where-Object { $_.Ordinal -in 104, 109 }) {
        throw "Steam Input gate must leave undocumented XInput ordinals 104 and 109 empty"
    }
}

# Start from an empty staging directory so renamed or dropped artifacts cannot ship through the
# application project's wildcard copy.
#
# Best-effort, NOT fatal: steam_input_gate.dll is injected into a running steam.exe
# and stays mapped until Steam restarts, so on a machine where the gate was used for
# diagnostics the delete fails with "access denied". That must not sink the whole
# build: warn, and let the copy below report the real problem if the artifact
# genuinely cannot be replaced.
if (Test-Path -LiteralPath $staging) {
    try {
        Remove-Item -Recurse -Force -LiteralPath $staging -ErrorAction Stop
    }
    catch {
        Write-Warning ("Could not clear $staging ($($_.Exception.Message)). " +
            "A staged DLL is probably loaded (the gate stays mapped in a running Steam until it restarts); " +
            "stale artifacts may survive this build.")
    }
}
New-Item -ItemType Directory -Force -Path $staging | Out-Null

# The gate is injected into steam.exe and the FFI library is what the managed binding loads.
# WSGM.Launch.exe is the launch wrapper; the library's CLI is not shipped.
foreach ($name in @("steam_input_gate.dll", "steam_input_lease_ffi.dll")) {
    $source = Join-Path $release $name
    if (-not (Test-Path $source)) { throw "Steam Input Lease did not produce $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $staging $name) -Force
}

Copy-Item -LiteralPath (Join-Path $library "LICENSE-MIT") `
    -Destination (Join-Path $staging "SteamInputLease-LICENSE-MIT.txt") -Force
Copy-Item -LiteralPath (Join-Path $library "THIRD_PARTY_LICENSES.md") `
    -Destination (Join-Path $staging "SteamInputLease-THIRD-PARTY-LICENSES.md") -Force

Write-Host "Steam Input Lease staged into src\WSGM\Native\SteamInputLease" -ForegroundColor Cyan
