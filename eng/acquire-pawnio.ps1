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

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
$pin = $lock.component
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

function Get-Pinned {
    param([string]$Uri, [string]$Path, [string]$Sha256)

    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $Path -UseBasicParsing
            break
        }
        catch {
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
            if ($attempt -ge 3) {
                throw "Downloading $Uri failed after $attempt attempts: $($_.Exception.Message)"
            }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) {
        Remove-Item -LiteralPath $Path -Force
        throw "$Uri digest $hash does not match the pinned $Sha256."
    }
}

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

    $archive = Join-Path $Destination "$($module.archive).partial"
    Get-Pinned $module.archiveUrl $archive $module.archiveSha256
    $partialModule = "$moduleTarget.partial"
    try {
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

if (Test-Path -LiteralPath $target -PathType Leaf) {
    Test-Pinned $target
    Write-Host "PawnIO $($pin.version) already present at $target"
    return
}

$partial = "$target.partial"
for ($attempt = 1; ; $attempt++) {
    try {
        Invoke-WebRequest -Uri $pin.assetUrl -OutFile $partial -UseBasicParsing
        break
    }
    catch {
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
        if ($attempt -ge 3) {
            throw "Downloading $($pin.assetUrl) failed after $attempt attempts: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds (2 * $attempt)
    }
}
try {
    Test-Pinned $partial
    Move-Item -LiteralPath $partial -Destination $target
}
catch {
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    throw
}

Write-Host "PawnIO $($pin.version) acquired at $target"
