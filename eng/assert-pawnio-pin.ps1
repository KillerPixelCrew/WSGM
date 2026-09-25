<#
.SYNOPSIS
    Fails when the PawnIO pin is malformed or an acquired installer disagrees with it.

.DESCRIPTION
    Device Lab installs PawnIO on a tester's machine from the installer pinned in
    external/pawnio/pawnio.lock.json, and the running tool checks the extracted file against the same
    lock. This check keeps that pin reviewable: an exact release URL on the upstream repository, a
    full SHA-256, a full signer thumbprint, silent arguments that never include -unrestricted, and the
    lock actually embedded by the Device Lab project. When eng/acquire-pawnio.ps1 has already fetched
    the installer, its digest and signer are checked too; the check never downloads anything.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$LockPath = (Join-Path $PSScriptRoot '..\external\pawnio\pawnio.lock.json'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\src\WSGM.DeviceLab\WSGM.DeviceLab.csproj'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot '..\artifacts\pawnio')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
$pin = $lock.component
$problems = [System.Collections.Generic.List[string]]::new()

if ($pin.id -ne 'pawnio') { $problems.Add("component id is '$($pin.id)', expected 'pawnio'") }
if ($pin.version -notmatch '^\d+\.\d+\.\d+$') { $problems.Add("version '$($pin.version)' is not an exact release") }
$expectedUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/$($pin.tag)/$($pin.asset)"
if ($pin.assetUrl -ne $expectedUrl) { $problems.Add("assetUrl '$($pin.assetUrl)' is not $expectedUrl") }
if ($pin.assetSha256 -notmatch '^[0-9A-F]{64}$') { $problems.Add('assetSha256 is not 64 upper-case hex digits') }
if ($pin.signerThumbprint -notmatch '^[0-9A-F]{40}$') { $problems.Add('signerThumbprint is not 40 upper-case hex digits') }
if ((@($pin.installArguments) -join ' ') -ne '-install -silent') { $problems.Add('installArguments must be exactly -install -silent') }
if ((@($pin.uninstallArguments) -join ' ') -ne '-uninstall -silent') { $problems.Add('uninstallArguments must be exactly -uninstall -silent') }
foreach ($argument in @($pin.installArguments) + @($pin.uninstallArguments)) {
    if ($argument -match 'unrestricted') { $problems.Add('an argument disables PawnIO module signature enforcement') }
}
if (-not [Version]::TryParse([string]$pin.minimumInstalledVersion, [ref]$null)) {
    $problems.Add("minimumInstalledVersion '$($pin.minimumInstalledVersion)' is not a version")
}

$project = Get-Content -LiteralPath $ProjectPath -Raw
if ($project -notmatch [regex]::Escape('external\pawnio\pawnio.lock.json')) {
    $problems.Add('the Device Lab project does not embed external\pawnio\pawnio.lock.json')
}

foreach ($module in @($lock.modules)) {
    $expectedArchive = "https://github.com/namazso/PawnIO.Modules/releases/download/$($module.tag)/$($module.archive)"
    if ($module.archiveUrl -ne $expectedArchive) { $problems.Add("module $($module.id) archiveUrl is not $expectedArchive") }
    if ($module.archiveSha256 -notmatch '^[0-9A-F]{64}$') { $problems.Add("module $($module.id) archiveSha256 is not 64 upper-case hex digits") }
    if ($module.memberSha256 -notmatch '^[0-9A-F]{64}$') { $problems.Add("module $($module.id) memberSha256 is not 64 upper-case hex digits") }
    $acquired = Join-Path $ArtifactDirectory $module.member
    if ((Test-Path -LiteralPath $acquired -PathType Leaf) -and
        (Get-FileHash -LiteralPath $acquired -Algorithm SHA256).Hash -ne $module.memberSha256) {
        $problems.Add("acquired $($module.member) does not match its pin")
    }
}

# KX.exe has no download and no signature: it is committed and pinned by digest alone.
$kxLockPath = Join-Path $PSScriptRoot '..\external\kx\kx.lock.json'
$kx = (Get-Content -LiteralPath $kxLockPath -Raw | ConvertFrom-Json).component
$kxPath = Join-Path (Split-Path -Parent $kxLockPath) $kx.file
if (-not (Test-Path -LiteralPath $kxPath -PathType Leaf)) {
    $problems.Add("external\kx\$($kx.file) is missing")
}
elseif ((Get-FileHash -LiteralPath $kxPath -Algorithm SHA256).Hash -ne $kx.sha256) {
    $problems.Add("external\kx\$($kx.file) does not match kx.lock.json")
}
if ($project -notmatch [regex]::Escape('external\kx\KX.exe')) {
    $problems.Add('the Device Lab project does not embed external\kx\KX.exe')
}

$installer = Join-Path $ArtifactDirectory $pin.asset
if (Test-Path -LiteralPath $installer -PathType Leaf) {
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    if ($hash -ne $pin.assetSha256) { $problems.Add("acquired installer digest $hash does not match the pin") }
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $pin.signerThumbprint) {
        $problems.Add("acquired installer signature is $($signature.Status), signer $($signature.SignerCertificate.Thumbprint)")
    }
}

if ($problems.Count -gt 0) {
    throw "PawnIO pin check failed:`n  $($problems -join "`n  ")"
}

Write-Host "PawnIO pin $($pin.version) is well formed."
