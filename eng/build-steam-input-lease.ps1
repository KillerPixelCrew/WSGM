<#
.SYNOPSIS
Builds the vendored Steam Input Lease library and stages its output for WSGM.

.DESCRIPTION
The library lives in this repository at native\SteamInput and is built from
source on every WSGM build, so the shipped gate can never drift from the code
next to it. Its build output is staged into src\WSGM\Native\SteamInputLease,
which WSGM.csproj copies beside the AOT executable and the installer ships.
That staging directory is generated and is not committed.

.PARAMETER Validate
Also run the library's own gates (clippy as errors, then the unit tests) before
building. Used by eng\verify.ps1; the release build skips them because
verify.ps1 has already run them in CI.
#>
[CmdletBinding()]
param(
    [switch]$Validate
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$library = Join-Path $root "native\SteamInput"
$manifest = Join-Path $library "Cargo.toml"
$staging = Join-Path $root "src\WSGM\Native\SteamInputLease"

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "Rust toolchain not found. Install it from https://rustup.rs — WSGM builds native\SteamInput from source."
}

if ($Validate) {
    cargo clippy --manifest-path $manifest --workspace --all-targets -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw "Steam Input Lease clippy check failed" }

    cargo test --manifest-path $manifest --workspace
    if ($LASTEXITCODE -ne 0) { throw "Steam Input Lease tests failed" }
}

cargo build --manifest-path $manifest --workspace --release
if ($LASTEXITCODE -ne 0) { throw "Steam Input Lease release build failed" }

New-Item -ItemType Directory -Force -Path $staging | Out-Null

# The gate is injected into steam.exe, the FFI library is what the managed
# binding loads, and the CLI is the wrapper users paste into Steam launch
# options. All three must ship together: the CLI resolves the gate beside itself.
$release = Join-Path $library "target\release"
foreach ($name in @("steam_input_gate.dll", "steam_input_lease_ffi.dll", "steam-input-lease.exe")) {
    $source = Join-Path $release $name
    if (-not (Test-Path $source)) { throw "Steam Input Lease did not produce $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $staging $name) -Force
}

Copy-Item -LiteralPath (Join-Path $library "LICENSE-MIT") `
    -Destination (Join-Path $staging "SteamInputLease-LICENSE-MIT.txt") -Force
Copy-Item -LiteralPath (Join-Path $library "THIRD_PARTY_LICENSES.md") `
    -Destination (Join-Path $staging "SteamInputLease-THIRD-PARTY-LICENSES.md") -Force

Write-Host "Steam Input Lease staged into src\WSGM\Native\SteamInputLease" -ForegroundColor Cyan
