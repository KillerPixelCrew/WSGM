<#
.SYNOPSIS
    Downloads and verifies the Device Lab release pinned in the lock file.

.DESCRIPTION
    `third_party/devicelab/devicelab.lock.json` names the exact release the installer's optional
    Device Lab component ships. This script reads it rather than restating it: a second copy of a
    pinned digest is a copy that can silently disagree with the reviewed one.

    The archive is verified by SHA-256 before it is expanded. A mismatch is fatal — this runs on the
    release machine, where the right answer is to stop and look rather than to ship bytes nobody
    reviewed.

    Nothing here is checked in. A generated destination carries an ownership marker and a complete
    payload inventory. Existing caller-owned directories without that marker are never erased.

.PARAMETER Destination
    Where to expand the verified tree. Defaults to the staging directory the build uses.

.PARAMETER LockPath
    The lock file to read. Defaults to the repository's own.

.PARAMETER Force
    Re-download even when the destination already holds the pinned version.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Destination = (Join-Path $PSScriptRoot '..\third_party\devicelab\staging'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$LockPath = (Join-Path $PSScriptRoot '..\third_party\devicelab\devicelab.lock.json'),

    [Parameter()]
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'acquisition-helpers.ps1')

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
$entry = $lock.component
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$ownerId = 'WSGM.acquire-devicelab/v1'
$assetName = [IO.Path]::GetFileName([string]$entry.asset)
$assetUri = $null
if ($assetName -cne [string]$entry.asset -or
    $assetName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.zip$' -or
    [long]$entry.assetBytes -le 0 -or
    [string]$entry.assetSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
    -not [Uri]::TryCreate([string]$entry.assetUrl, [UriKind]::Absolute, [ref]$assetUri) -or
    $assetUri.Scheme -cne [Uri]::UriSchemeHttps -or
    [Uri]::UnescapeDataString([IO.Path]::GetFileName($assetUri.AbsolutePath)) -cne $assetName) {
    throw 'The Device Lab lock has an unsafe asset name, URL, size or digest.'
}

function Assert-DeviceLabPayload([string]$Root) {
    $applicationName = [IO.Path]::GetFileNameWithoutExtension([string]$entry.executable)
    $requiredFiles = @(
        [string]$entry.executable,
        "$applicationName.dll",
        "$applicationName.deps.json",
        "$applicationName.runtimeconfig.json",
        'LICENSE.txt',
        'THIRD_PARTY_NOTICES.md',
        'DotNetRuntime-LICENSE.txt',
        'DotNetRuntime-THIRD-PARTY-NOTICES.txt')
    foreach ($required in $requiredFiles) {
        if ([IO.Path]::IsPathRooted($required) -or
            [IO.Path]::GetFileName($required) -cne $required) {
            throw "The Device Lab lock or payload names an unsafe required file: $required"
        }
        $path = Join-Path $Root $required
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -le 0) {
            throw "The verified Device Lab archive did not contain a non-empty $required."
        }
    }
}

if (-not $Force -and (Test-WsgmPayloadCache `
    -Root $destinationRoot `
    -OwnerId $ownerId `
    -AssetSha256 $entry.assetSha256 `
    -ValidatePayload ${function:Assert-DeviceLabPayload})) {
    Write-Information "Device Lab $($entry.version) is already staged." -InformationAction Continue
    return $destinationRoot
}

Assert-WsgmDestinationReplaceable `
    -Root $destinationRoot `
    -OwnerId $ownerId

$destinationParent = Split-Path -Path $destinationRoot -Parent
$destinationLeaf = Split-Path -Path $destinationRoot -Leaf
if ([string]::IsNullOrWhiteSpace($destinationParent) -or
    [string]::IsNullOrWhiteSpace($destinationLeaf)) {
    throw "Device Lab destination must be a named directory below a filesystem root: $destinationRoot"
}
New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
$operationId = [Guid]::NewGuid().ToString('N')
$stagingRoot = Join-Path $destinationParent (
    ".$destinationLeaf.staging-$PID-$operationId")
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) (
    'WSGM-DeviceLab-{0}-{1}-{2}' -f $PID, $operationId, $assetName)

Write-Information "Acquiring Device Lab $($entry.version)" -InformationAction Continue
try {
    # The default progress renderer costs more than the download on an asset this size.
    $previousProgress = $ProgressPreference
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $assetUri.AbsoluteUri -OutFile $archivePath -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previousProgress
    }

    $actualBytes = (Get-Item -LiteralPath $archivePath).Length
    if ($actualBytes -ne [long]$entry.assetBytes) {
        throw "Size mismatch for $assetName`: expected $($entry.assetBytes), got $actualBytes."
    }
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -cne $entry.assetSha256) {
        throw "Hash mismatch for $assetName`: expected $($entry.assetSha256), got $actualHash."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingRoot -Force
    Assert-WsgmPayloadHasNoLinks -Root $stagingRoot
    Assert-DeviceLabPayload $stagingRoot
    Initialize-WsgmPayloadMetadata `
        -Root $stagingRoot `
        -OwnerId $ownerId `
        -AssetSha256 $entry.assetSha256
    if (-not (Test-WsgmPayloadCache `
        -Root $stagingRoot `
        -OwnerId $ownerId `
        -AssetSha256 $entry.assetSha256 `
        -ValidatePayload ${function:Assert-DeviceLabPayload})) {
        throw 'The staged Device Lab payload failed its complete inventory check.'
    }

    Install-WsgmPayloadAtomically `
        -StagingRoot $stagingRoot `
        -DestinationRoot $destinationRoot `
        -OwnerId $ownerId
    Write-Information "  verified and staged $assetName ($actualHash)" -InformationAction Continue
}
finally {
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

return $destinationRoot
