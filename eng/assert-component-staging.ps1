<#[
.SYNOPSIS
    Fails when isolated release staging contains a boundary or package-safety violation.
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

function Require-File([string]$RelativePath) {
    $path = Join-Path $outputFull $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required staged artifact is missing: $RelativePath"
    }
}

function Assert-NoLinks([string]$Directory) {
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force -Recurse) {
        if ($entry.LinkType -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Release staging may not contain links or reparse points: $($entry.FullName)"
        }
    }
}

Require-File "App\WSGM.exe"
Require-File "App\WSGM.Launch.exe"
Require-File "App\WSGM.LogonService.exe"
Require-File "DeviceHost\WSGM.DeviceHost.exe"
Require-File "Tools\DeviceLab\WSGM.DeviceLab.exe"
Require-File "Tools\CommandLine\wsgm-device.exe"
Require-File "Tools\ProbeHost\WSGM.Device.ProbeHost.exe"

foreach ($directory in @("App", "DeviceHost", "Tools", "Packages")) {
    Assert-NoLinks (Join-Path $outputFull $directory)
}

& "$PSScriptRoot\check-aot-isolation.ps1" -OutputDirectory (Join-Path $outputFull "App")

$hostForbidden = @(
    Get-ChildItem -LiteralPath (Join-Path $outputFull "DeviceHost") -File -Recurse |
    Where-Object { $_.Name -in @("WSGM.exe", "WSGM.Launch.exe", "WSGM.LogonService.exe") `
        -or $_.Name -like "WSGM.Device.Msi.*" }
)
if ($hostForbidden.Count -gt 0) {
    throw "DeviceHost staging contains app or plugin binaries: $($hostForbidden.Name -join ', ')"
}

$packageRoots = @(
    Get-ChildItem -LiteralPath (Join-Path $outputFull "Packages") -Directory |
    ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory } |
    Sort-Object FullName
)
if ($packageRoots.Count -eq 0) {
    throw "No plugin packages were staged."
}

$forbiddenExtensions = @(
    ".pdb", ".cs", ".csx", ".ps1", ".psm1", ".pfx", ".p12", ".snk", ".key",
    ".pem", ".pvk", ".jks", ".keystore", ".etl", ".evtx", ".pcap", ".pcapng",
    ".dmp", ".dump", ".wsgmcap", ".zip", ".7z"
)
$textExtensions = @(".json", ".xml", ".config", ".txt", ".md", ".js", ".css", ".svg")
$localPathPattern = '(?i)(?:[A-Z]:[\\/](?:Users|Coding|Repos?|Source|Worktrees?)[\\/]|\\\\\?\\[A-Z]:\\)'
$secretPattern = '(?i)(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[=:]\s*["'']?[^\s"'']{8,}'

foreach ($packageRoot in $packageRoots) {
    Require-File ([IO.Path]::GetRelativePath($outputFull, (Join-Path $packageRoot.FullName "plugin.wsgm.json")))
    Require-File ([IO.Path]::GetRelativePath($outputFull, (Join-Path $packageRoot.FullName "package-files.wsgm.json")))
    Require-File ([IO.Path]::GetRelativePath($outputFull, (Join-Path $packageRoot.FullName "installed.wsgm.json")))

    $manifest = Get-Content -LiteralPath (Join-Path $packageRoot.FullName "plugin.wsgm.json") -Raw |
        ConvertFrom-Json -Depth 32
    if ($packageRoot.Parent.Name -cne [string]$manifest.id -or
        $packageRoot.Name -cne [string]$manifest.version) {
        throw "Plugin package path does not match its manifest identity: $($packageRoot.FullName)"
    }
    Require-File ([IO.Path]::GetRelativePath(
        $outputFull,
        (Join-Path $packageRoot.FullName ([string]$manifest.entryPoint))))

    $files = @(Get-ChildItem -LiteralPath $packageRoot.FullName -File -Recurse | Sort-Object FullName)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($packageRoot.FullName, $file.FullName).Replace("\", "/")
        if ($file.Extension -in $forbiddenExtensions) {
            throw "Plugin package contains source/debug/capture/key material: $relative"
        }
        if ($file.Name -in @(".env", "secrets.json", "appsettings.Development.json", "NuGet.Config")) {
            throw "Plugin package contains a credential-bearing developer file: $relative"
        }
        if ($relative -match '(?i)(?:^|/)(?:captures?|raw[-_]?evidence|fixtures?|recipes?)(?:/|$)') {
            throw "Plugin package contains a source-capture or evidence directory: $relative"
        }
        if ($file.Name -in @("WSGM.exe", "WSGM.Launch.exe", "WSGM.LogonService.exe",
            "WSGM.DeviceHost.exe", "wsgm-device.exe", "WSGM.DeviceLab.exe")) {
            throw "Plugin package contains an unrelated WSGM executable: $relative"
        }
        if ($file.Extension -in $textExtensions -and $file.Length -le 4MB) {
            $text = Get-Content -LiteralPath $file.FullName -Raw
            if ($text -match $localPathPattern) {
                throw "Plugin package leaks a local developer path: $relative"
            }
            if ($text -match $secretPattern) {
                throw "Plugin package contains a secret-shaped assignment: $relative"
            }
        }
    }

    $hashRecord = Get-Content -LiteralPath (Join-Path $packageRoot.FullName "package-files.wsgm.json") `
        -Raw | ConvertFrom-Json -Depth 16
    $actualFiles = @($files | Where-Object {
        $_.Name -notin @("package-files.wsgm.json", "installed.wsgm.json")
    } | ForEach-Object {
        [IO.Path]::GetRelativePath($packageRoot.FullName, $_.FullName).Replace("\", "/")
    } | Sort-Object)
    $recordedFiles = @($hashRecord.files | ForEach-Object { [string]$_.path } | Sort-Object)
    if (Compare-Object -ReferenceObject $actualFiles -DifferenceObject $recordedFiles) {
        throw "Plugin package hash record does not cover its exact file set: $($packageRoot.FullName)"
    }
    foreach ($entry in $hashRecord.files) {
        $filePath = Join-Path $packageRoot.FullName ([string]$entry.path)
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actualHash -cne [string]$entry.sha256) {
            throw "Plugin package hash mismatch: $($entry.path)"
        }
    }

    $installRecord = Get-Content -LiteralPath (Join-Path $packageRoot.FullName "installed.wsgm.json") `
        -Raw | ConvertFrom-Json -Depth 32
    if ([int]$installRecord.schemaVersion -ne 1 -or
        [string]$installRecord.packageId -cne [string]$manifest.id -or
        [string]$installRecord.version -cne [string]$manifest.version -or
        [int]$installRecord.trustTier -ne 0) {
        throw "Reviewed install grant identity is invalid: $($packageRoot.FullName)"
    }
    $grantedFiles = @($installRecord.fileHashes.psobject.Properties.Name | Sort-Object)
    $installedFiles = @($files | Where-Object { $_.Name -cne "installed.wsgm.json" } |
        ForEach-Object {
            [IO.Path]::GetRelativePath($packageRoot.FullName, $_.FullName).Replace("\", "/")
        } | Sort-Object)
    if (Compare-Object -ReferenceObject $installedFiles -DifferenceObject $grantedFiles) {
        throw "Reviewed install grant does not cover its exact file set: $($packageRoot.FullName)"
    }
    foreach ($property in $installRecord.fileHashes.psobject.Properties) {
        $actualHash = (Get-FileHash `
            -LiteralPath (Join-Path $packageRoot.FullName $property.Name) `
            -Algorithm SHA256).Hash
        if ($actualHash -cne [string]$property.Value) {
            throw "Reviewed install grant hash mismatch: $($property.Name)"
        }
    }
}

Write-Host "Component isolation and package staging assertions passed."
