<#
.SYNOPSIS
    Downloads the pinned PawnIO installer and modules into artifacts/pawnio and verifies them.

.DESCRIPTION
    Reads external/pawnio/pawnio.lock.json, downloads the installer when it is missing, and fails
    when its SHA-256 or Authenticode signer differs from the pin. Each pinned module is extracted from
    its release archive after the archive digest is checked, and the module digest is checked too.
    Files that already match are left alone, so the script is safe to run before every Device Lab
    publish.
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
. (Join-Path $PSScriptRoot 'build-common.ps1')

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
$pin = $lock.component
$target = Join-Path $Destination $pin.asset

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
foreach ($module in @($lock.modules)) {
    $moduleTarget = Join-Path $Destination $module.member
    if (Test-Path -LiteralPath $moduleTarget -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $moduleTarget -Algorithm SHA256).Hash -ne $module.memberSha256) {
            throw "$moduleTarget does not match the pinned $($module.memberSha256)."
        }
        Write-Host "PawnIO module $($module.id) $($module.tag) already present at $moduleTarget"
        continue
    }

    $archive = Join-Path $Destination $module.archive
    $partialModule = "$moduleTarget.partial"
    try {
        Get-PinnedAsset -Url $module.archiveUrl -Path $archive -Sha256 $module.archiveSha256
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq $module.member } | Select-Object -First 1
            if ($null -eq $entry) { throw "$($module.archive) has no $($module.member)." }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $partialModule, $true)
        }
        finally {
            $zip.Dispose()
        }
        if ((Get-FileHash -LiteralPath $partialModule -Algorithm SHA256).Hash -ne $module.memberSha256) {
            throw "$($module.member) digest does not match the pinned $($module.memberSha256)."
        }
        Move-Item -LiteralPath $partialModule -Destination $moduleTarget
    }
    finally {
        Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $partialModule -Force -ErrorAction SilentlyContinue
    }
    Write-Host "PawnIO module $($module.id) $($module.tag) acquired at $moduleTarget"
}

Get-PinnedAsset -Url $pin.assetUrl -Path $target -Sha256 $pin.assetSha256 `
    -SignerThumbprint $pin.signerThumbprint
Write-Host "PawnIO $($pin.version) verified at $target"
