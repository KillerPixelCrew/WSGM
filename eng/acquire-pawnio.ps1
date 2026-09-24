<#
.SYNOPSIS
    Downloads the pinned PawnIO installer into artifacts/pawnio and verifies it.

.DESCRIPTION
    Reads external/pawnio/pawnio.lock.json, downloads the asset when it is missing, and fails when
    the file's SHA-256 or Authenticode signer differs from the pin. A file that already matches is
    left alone, so the script is safe to run before every Device Lab publish.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$LockPath = (Join-Path $PSScriptRoot '..\external\pawnio\pawnio.lock.json'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Destination = (Join-Path $PSScriptRoot '..\artifacts\pawnio')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pin = (Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json).component
$target = Join-Path $Destination $pin.asset

function Test-Pinned {
    param([string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($hash -ne $pin.assetSha256) {
        throw "PawnIO installer digest $hash does not match the pinned $($pin.assetSha256)."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $pin.signerThumbprint) {
        throw "PawnIO installer signature is $($signature.Status) with signer $($signature.SignerCertificate.Thumbprint); expected $($pin.signerThumbprint)."
    }
}

if (Test-Path -LiteralPath $target -PathType Leaf) {
    Test-Pinned $target
    Write-Host "PawnIO $($pin.version) already present at $target"
    return
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$partial = "$target.partial"
Invoke-WebRequest -Uri $pin.assetUrl -OutFile $partial -UseBasicParsing
try {
    Test-Pinned $partial
    Move-Item -LiteralPath $partial -Destination $target
}
catch {
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    throw
}

Write-Host "PawnIO $($pin.version) acquired at $target"
