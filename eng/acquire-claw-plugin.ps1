<#
.SYNOPSIS
    Downloads, verifies and expands the built-in device package pinned in the lock file.

.DESCRIPTION
    `third_party/claw-plugin/claw-plugin.lock.json` names the exact release the installer's optional
    Device Integration component ships. This script reads it rather than restating it: a second copy
    of a pinned digest is a copy that can silently disagree with the reviewed one.

    The archive is verified by SHA-256 before it is expanded. A mismatch is fatal — this runs on the
    release machine, where the right answer is to stop and look rather than to ship a device package
    nobody reviewed.

    Nothing here is checked in. A generated destination carries an ownership marker and a complete
    payload inventory. Existing caller-owned directories without that marker are never erased.

.PARAMETER Destination
    Where to expand the verified package. Defaults to the staging directory the build uses.

.PARAMETER LockPath
    The lock file to read. Defaults to the repository's own.

.PARAMETER Force
    Re-download even when the destination already holds the pinned version.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Destination = (Join-Path $PSScriptRoot '..\third_party\claw-plugin\staging'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$LockPath = (Join-Path $PSScriptRoot '..\third_party\claw-plugin\claw-plugin.lock.json'),

    [Parameter()]
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'acquisition-helpers.ps1')

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
$entry = $lock.component
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$ownerId = 'WSGM.acquire-claw-plugin/v1'
$assetName = [IO.Path]::GetFileName([string]$entry.asset)
$assetUri = $null
if ($assetName -cne [string]$entry.asset -or
    $assetName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.wsgmpkg$' -or
    [long]$entry.assetBytes -le 0 -or
    [string]$entry.assetSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
    [int]$entry.glyphFiles -lt 0 -or
    -not [Uri]::TryCreate([string]$entry.assetUrl, [UriKind]::Absolute, [ref]$assetUri) -or
    $assetUri.Scheme -cne [Uri]::UriSchemeHttps -or
    [Uri]::UnescapeDataString([IO.Path]::GetFileName($assetUri.AbsolutePath)) -cne $assetName) {
    throw 'The built-in package lock has an unsafe asset name, URL, size, digest or glyph count.'
}

function Assert-ClawPluginPayload([string]$Root) {
    $requiredFiles = @(
        'plugin.wsgm.json',
        [string]$entry.entryAssembly,
        'LICENSE.txt',
        'PROVENANCE.md',
        'THIRD_PARTY_NOTICES.md')
    foreach ($required in $requiredFiles) {
        if ([IO.Path]::IsPathRooted($required) -or
            [IO.Path]::GetFileName($required) -cne $required) {
            throw "The built-in package lock or manifest names an unsafe required file: $required"
        }
        $path = Join-Path $Root $required
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -le 0) {
            throw "The verified package did not contain a non-empty $required."
        }
    }

    $manifestPath = Join-Path $Root 'plugin.wsgm.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 32
    if ([string]$manifest.id -cne [string]$entry.packageId) {
        throw "The package manifest declares id '$($manifest.id)', not '$($entry.packageId)'."
    }
    if ([string]$manifest.version -cne [string]$entry.version) {
        throw "The package manifest declares version '$($manifest.version)', not '$($entry.version)'."
    }
    if ([string]$manifest.entryAssembly -cne [string]$entry.entryAssembly) {
        throw "The package manifest declares entry assembly '$($manifest.entryAssembly)', not '$($entry.entryAssembly)'."
    }

    $glyphRoot = Join-Path $Root 'glyphs'
    $glyphFiles = @(
        Get-ChildItem -LiteralPath $glyphRoot -File -Recurse -ErrorAction SilentlyContinue)
    if ($glyphFiles.Count -ne [int]$entry.glyphFiles) {
        throw "The package contains $($glyphFiles.Count) glyph files; the lock requires $($entry.glyphFiles)."
    }
    foreach ($glyph in $glyphFiles) {
        if ($glyph.Length -le 0) {
            throw "The package contains an empty glyph file: $($glyph.FullName)"
        }
    }
}

if (-not $Force -and (Test-WsgmPayloadCache `
    -Root $destinationRoot `
    -OwnerId $ownerId `
    -AssetSha256 $entry.assetSha256 `
    -ValidatePayload ${function:Assert-ClawPluginPayload})) {
    Write-Information "$($entry.packageId) $($entry.version) is already staged." -InformationAction Continue
    return $destinationRoot
}

Assert-WsgmDestinationReplaceable `
    -Root $destinationRoot `
    -OwnerId $ownerId

$destinationParent = Split-Path -Path $destinationRoot -Parent
$destinationLeaf = Split-Path -Path $destinationRoot -Leaf
if ([string]::IsNullOrWhiteSpace($destinationParent) -or
    [string]::IsNullOrWhiteSpace($destinationLeaf)) {
    throw "Built-in package destination must be a named directory below a filesystem root: $destinationRoot"
}
New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
$operationId = [Guid]::NewGuid().ToString('N')
$stagingRoot = Join-Path $destinationParent (
    ".$destinationLeaf.staging-$PID-$operationId")
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) (
    'WSGM-ClawPlugin-{0}-{1}-{2}' -f $PID, $operationId, $assetName)

Write-Information "Acquiring $($entry.packageId) $($entry.version)" -InformationAction Continue
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

    # A .wsgmpkg is the deterministic tar Device Lab's `pack` produces.
    $archiveEntries = @(& tar -tf $archivePath)
    if ($LASTEXITCODE -ne 0) {
        throw "Reading $assetName failed."
    }
    foreach ($archiveEntry in $archiveEntries) {
        $normalized = ([string]$archiveEntry).Replace('\', '/').TrimEnd('/')
        if ($normalized.Length -eq 0) {
            continue
        }
        $segments = @($normalized.Split('/', [StringSplitOptions]::RemoveEmptyEntries))
        if ([IO.Path]::IsPathRooted($normalized) -or
            $normalized -match '^[A-Za-z]:' -or
            $segments -contains '..' -or
            $normalized -in @(
                $WsgmAcquisitionOwnerMarkerName,
                $WsgmAcquisitionInventoryName,
                $WsgmAcquisitionStampName)) {
            throw "The package archive contains an unsafe path: $archiveEntry"
        }
    }

    & tar -xf $archivePath -C $stagingRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Extracting $assetName failed."
    }

    Assert-WsgmPayloadHasNoLinks -Root $stagingRoot
    Assert-ClawPluginPayload $stagingRoot
    Initialize-WsgmPayloadMetadata `
        -Root $stagingRoot `
        -OwnerId $ownerId `
        -AssetSha256 $entry.assetSha256
    if (-not (Test-WsgmPayloadCache `
        -Root $stagingRoot `
        -OwnerId $ownerId `
        -AssetSha256 $entry.assetSha256 `
        -ValidatePayload ${function:Assert-ClawPluginPayload})) {
        throw 'The staged built-in package failed its complete inventory check.'
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
