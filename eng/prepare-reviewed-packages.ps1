<#
.SYNOPSIS
    Creates installer-owned grants for the exact reviewed plugin bytes in publish\Packages.

.DESCRIPTION
    A valid Authenticode signature enables a bundled reviewed package and pins its certificate
    subject and thumbprint. Unsigned developer builds still produce an installable, inert package:
    the grant remains disabled and runtime discovery fails closed without loading its assembly.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
$expectedOutput = [IO.Path]::GetFullPath((Join-Path $root "publish"))
if (-not $outputFull.Equals($expectedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Reviewed package grants may only be written to the repository publish tree."
}

$packagesRoot = Join-Path $outputFull "Packages"
if (-not (Test-Path -LiteralPath $packagesRoot -PathType Container)) {
    throw "Reviewed package staging is missing: $packagesRoot"
}

function Write-DeterministicJson([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 64 -Compress
    [IO.File]::WriteAllText($Path, $json + "`n", [Text.UTF8Encoding]::new($false))
}

$packageRoots = @(
    Get-ChildItem -LiteralPath $packagesRoot -Directory |
    Sort-Object Name |
    ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory | Sort-Object Name }
)
if ($packageRoots.Count -eq 0) {
    throw "No reviewed package versions were staged."
}

foreach ($packageRoot in $packageRoots) {
    $manifestPath = Join-Path $packageRoot.FullName "plugin.wsgm.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 64
    $entryPointPath = Join-Path $packageRoot.FullName ([string]$manifest.entryPoint)
    $signature = Get-AuthenticodeSignature -LiteralPath $entryPointPath
    $enabled = $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid -and
        $null -ne $signature.SignerCertificate
    $publisherSubject = $null
    $publisherThumbprint = $null
    if ($enabled) {
        $publisherSubject = $signature.SignerCertificate.Subject
        $publisherThumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
        $manifest.publisher = $publisherSubject
        Write-DeterministicJson $manifestPath $manifest
    }

    $payloadFiles = @(
        Get-ChildItem -LiteralPath $packageRoot.FullName -File -Recurse |
        Where-Object { $_.Name -notin @("installed.wsgm.json", "package-files.wsgm.json") } |
        Sort-Object FullName
    )
    $payloadRecords = @($payloadFiles | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($packageRoot.FullName, $_.FullName).Replace("\", "/")
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $packageHashRecord = [ordered]@{
        schemaVersion = 1
        packageId = [string]$manifest.id
        packageVersion = [string]$manifest.version
        configuration = "Release"
        runtimeIdentifier = "win-x64"
        files = $payloadRecords
    }
    Write-DeterministicJson `
        (Join-Path $packageRoot.FullName "package-files.wsgm.json") `
        $packageHashRecord

    $fileHashes = [ordered]@{}
    Get-ChildItem -LiteralPath $packageRoot.FullName -File -Recurse |
        Where-Object { $_.Name -cne "installed.wsgm.json" } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath(
                $packageRoot.FullName,
                $_.FullName).Replace("\", "/")
            $fileHashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }

    $installRecord = [ordered]@{
        schemaVersion = 1
        packageId = [string]$manifest.id
        version = [string]$manifest.version
        trustTier = 0
        enabled = $enabled
        publisherSubject = $publisherSubject
        publisherThumbprint = $publisherThumbprint
        fileHashes = $fileHashes
        installedAt = "1970-01-01T00:00:00+00:00"
    }
    Write-DeterministicJson `
        (Join-Path $packageRoot.FullName "installed.wsgm.json") `
        $installRecord

    if ($enabled) {
        Write-Host "Prepared enabled reviewed package $($manifest.id) $($manifest.version)."
    }
    else {
        Write-Warning "Reviewed package $($manifest.id) $($manifest.version) is unsigned and will remain disabled."
    }
}
