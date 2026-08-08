<#
.SYNOPSIS
Builds the radio helper library and stages it for WSGM.

.DESCRIPTION
The library lives in this repository at native\Radio and is built from source on
every WSGM build, so the shipped helper can never drift from the code next to
it. Its output is staged into src\WSGM\Native\Radio, which WSGM.csproj copies
beside the AOT executable and the installer ships. That staging directory is
generated and is not committed.

The helper exists because WSGM's executable is NativeAOT with managed COM
interop disabled: WinRT radio and Bluetooth pairing cannot be reached from C#
at all, so this owns those calls behind a flat C ABI. Same arrangement as
native\VolumeControl and the Steam Input lease.

.PARAMETER Validate
Also run the library's own gates (clippy as errors, then the unit tests) before
building. Used by eng\verify.ps1; the release build skips them because
verify.ps1 has already run them.
#>
[CmdletBinding()]
param(
    [switch]$Validate
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$library = Join-Path $root "native\Radio"
$manifest = Join-Path $library "Cargo.toml"
$staging = Join-Path $root "src\WSGM\Native\Radio"

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "Rust toolchain not found. Install it from https://rustup.rs — WSGM builds native\Radio from source."
}

if ($Validate) {
    cargo clippy --manifest-path $manifest --workspace --all-targets -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw "Radio helper clippy check failed" }

    cargo test --manifest-path $manifest --workspace
    if ($LASTEXITCODE -ne 0) { throw "Radio helper tests failed" }
}

cargo build --manifest-path $manifest --workspace --release
if ($LASTEXITCODE -ne 0) { throw "Radio helper release build failed" }

New-Item -ItemType Directory -Force -Path $staging | Out-Null

# Cargo requires a snake_case crate name; the shipped file matches the naming of
# WSGM.VolumeControl.dll beside it, which is what LibraryImport resolves.
$release = Join-Path $library "target\release"
$source = Join-Path $release "wsgm_radio.dll"
if (-not (Test-Path $source)) { throw "Radio helper did not produce wsgm_radio.dll" }
Copy-Item -LiteralPath $source -Destination (Join-Path $staging "WSGM.Radio.dll") -Force

# The probe is a diagnostic the user runs on the device to answer questions the
# documentation does not settle: whether radio control works elevated with no
# shell, and whether the Windows 11 24H2 location gate blocks the Wi-Fi scan.
$probe = Join-Path $release "wsgm-radio-probe.exe"
if (-not (Test-Path $probe)) { throw "Radio helper did not produce wsgm-radio-probe.exe" }
Copy-Item -LiteralPath $probe -Destination (Join-Path $staging "WSGM.RadioProbe.exe") -Force

Write-Host "Radio helper staged into src\WSGM\Native\Radio" -ForegroundColor Cyan
