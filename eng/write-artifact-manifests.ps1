<#[
.SYNOPSIS
    Writes deterministic per-component and release hash manifests for a finished publish tree.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [Parameter(Mandatory)]
    [string]$Version,

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
$expectedOutput = [IO.Path]::GetFullPath((Join-Path $root "publish"))
if (-not $outputFull.Equals($expectedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release manifests may only replace the repository's exact publish\Manifests directory."
}

$manifestDirectory = Join-Path $outputFull "Manifests"
if (Test-Path -LiteralPath $manifestDirectory) {
    Remove-Item -LiteralPath $manifestDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $manifestDirectory | Out-Null

function Write-DeterministicJson([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 12 -Compress
    [IO.File]::WriteAllText($Path, $json + "`n", [Text.UTF8Encoding]::new($false))
}

function Get-FileRecord([IO.FileInfo]$File, [string]$BasePath) {
    $signatureStatus = $null
    $signerSubject = $null
    $signerThumbprint = $null
    if ($File.Extension -in @(".exe", ".dll")) {
        $signature = Get-AuthenticodeSignature -LiteralPath $File.FullName
        $signatureStatus = [string]$signature.Status
        if ($null -ne $signature.SignerCertificate) {
            $signerSubject = $signature.SignerCertificate.Subject
            $signerThumbprint = $signature.SignerCertificate.Thumbprint
        }
    }
    return [ordered]@{
        path = [IO.Path]::GetRelativePath($BasePath, $File.FullName).Replace("\", "/")
        length = $File.Length
        sha256 = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash
        authenticodeStatus = $signatureStatus
        signerSubject = $signerSubject
        signerThumbprint = $signerThumbprint
    }
}

$componentSources = @(
    Get-ChildItem -LiteralPath $outputFull -Directory |
    Where-Object { $_.Name -cne "Manifests" } |
    ForEach-Object {
        [ordered]@{ Name = $_.Name; Base = $_.FullName; Files = @(Get-ChildItem -LiteralPath $_.FullName -File -Recurse) }
    }
)
$rootFiles = @(Get-ChildItem -LiteralPath $outputFull -File)
if ($rootFiles.Count -gt 0) {
    $componentSources += [ordered]@{ Name = "Installer"; Base = $outputFull; Files = $rootFiles }
}

$releaseComponents = @()
foreach ($source in $componentSources | Sort-Object { $_.Name }) {
    $records = @($source.Files | Sort-Object FullName | ForEach-Object {
        Get-FileRecord $_ $source.Base
    })
    $component = [ordered]@{
        schemaVersion = 1
        releaseVersion = $Version
        component = $source.Name
        configuration = $Configuration
        runtimeIdentifier = $RuntimeIdentifier
        files = $records
    }
    $safeName = ([string]$source.Name).ToLowerInvariant() -replace '[^a-z0-9.-]', '-'
    $manifestName = "$safeName.manifest.json"
    $manifestPath = Join-Path $manifestDirectory $manifestName
    Write-DeterministicJson $manifestPath $component
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    $totalBytes = ($records | Measure-Object -Property length -Sum).Sum
    if ($null -eq $totalBytes) { $totalBytes = 0 }
    $releaseComponents += [ordered]@{
        name = $source.Name
        manifest = "Manifests/$manifestName"
        manifestSha256 = $manifestHash
        fileCount = $records.Count
        totalBytes = $totalBytes
    }
}

$release = [ordered]@{
    schemaVersion = 1
    releaseVersion = $Version
    configuration = $Configuration
    runtimeIdentifier = $RuntimeIdentifier
    components = $releaseComponents
}
$releasePath = Join-Path $manifestDirectory "release.manifest.json"
Write-DeterministicJson $releasePath $release
$releaseHash = (Get-FileHash -LiteralPath $releasePath -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    (Join-Path $manifestDirectory "release.manifest.sha256"),
    "$releaseHash *release.manifest.json`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "Release manifest SHA-256: $releaseHash"
