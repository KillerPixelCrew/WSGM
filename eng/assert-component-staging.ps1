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
Require-File "App\LICENSE.txt"
Require-File "Tools\DeviceLab\wsgm-device.exe"
Require-File "Tools\DeviceLab\THIRD_PARTY_NOTICES.md"
Require-File "Tools\DeviceLab\DotNetRuntime-LICENSE.txt"
Require-File "Tools\DeviceLab\DotNetRuntime-THIRD-PARTY-NOTICES.txt"

foreach ($directory in @("App", "Tools", "Packages")) {
    Assert-NoLinks (Join-Path $outputFull $directory)
}

$packagesDirectory = Join-Path $outputFull "Packages"
if (@(Get-ChildItem -LiteralPath $packagesDirectory -Directory).Count -ne 0) {
    throw "Plugin packages are staged as .wsgmpkg files, never as unpacked directories."
}
$packageFiles = @(
    Get-ChildItem -LiteralPath $packagesDirectory -File -Filter "*.wsgmpkg" |
    Sort-Object FullName
)
if ($packageFiles.Count -eq 0) {
    throw "No plugin package was staged."
}

# bundle.json must describe exactly the staged files, byte for byte.
$bundle = Get-Content -LiteralPath (Join-Path $outputFull "bundle.json") -Raw | ConvertFrom-Json -Depth 32
$listed = @($bundle.plugins)
if ($listed.Count -ne $packageFiles.Count) {
    throw "bundle.json lists $($listed.Count) plugin(s) but $($packageFiles.Count) package file(s) are staged."
}
foreach ($entry in $listed) {
    $staged = Join-Path $packagesDirectory ([string]$entry.file)
    if (-not (Test-Path -LiteralPath $staged -PathType Leaf)) {
        throw "bundle.json names $($entry.file), which is not staged."
    }
    if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne ([string]$entry.sha256).ToUpperInvariant()) {
        throw "bundle.json's hash for $($entry.file) does not match the staged file."
    }
}

$forbiddenExtensions = @(
    ".pdb", ".cs", ".csx", ".ps1", ".psm1", ".pfx", ".p12", ".snk", ".key",
    ".pem", ".pvk", ".jks", ".keystore", ".etl", ".evtx", ".pcap", ".pcapng",
    ".dmp", ".dump", ".wsgmcap", ".zip", ".7z"
)
$textExtensions = @(".json", ".xml", ".config", ".txt", ".md", ".js", ".css", ".svg", ".h")
# Separators are [\\/]+ so a JSON-escaped path (C:\\Users\\...) still matches. Besides the usual
# developer roots, the pattern names this checkout's own root and the CI workspace shape (D:\a\),
# because a generic list missed a root such as E:\SourceCode\.
$separator = '[\\/]+'
$rootPattern = (($root.TrimEnd('\', '/') -split '[\\/]') | ForEach-Object { [regex]::Escape($_) }) -join $separator
$localPathPattern = "(?i)(?:$rootPattern|[A-Z]:$separator(?:Users|Coding|Repos?|Source\w*|Src|Dev|Projects?|Worktrees?|Git|a)$separator|\\\\\?\\[A-Z]:\\)"
$secretPattern = '(?i)(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[=:]\s*["'']?[^\s"'']{8,}'

# Text leak checks shared by every shipped tree.
function Assert-NoLeaks([IO.FileInfo]$File, [string]$Label) {
    if ($File.Extension -notin $textExtensions -or $File.Length -gt 4MB) {
        return
    }
    $text = Get-Content -LiteralPath $File.FullName -Raw
    if ($text -match $localPathPattern) {
        throw "$Label leaks a local developer path: $($File.FullName)"
    }
    if ($text -match $secretPattern) {
        throw "$Label contains a secret-shaped assignment: $($File.FullName)"
    }
}

# Material that must never reach a user, wherever it is shipped from.
function Assert-NoDeveloperMaterial([IO.FileInfo]$File, [string]$Label) {
    if ($File.Extension -in $forbiddenExtensions) {
        throw "$Label contains source/debug/capture/key material: $($File.FullName)"
    }
    if ($File.Name -in @(".env", "secrets.json", "appsettings.Development.json", "NuGet.Config")) {
        throw "$Label contains a credential-bearing developer file: $($File.FullName)"
    }
}

# The installer copies Tools recursively, so everything in it ships.
foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $outputFull "Tools") -File -Recurse)) {
    Assert-NoDeveloperMaterial $file "Tools staging"
    Assert-NoLeaks $file "Tools staging"
}

# App is shipped by an explicit allowlist in installer\WSGM.iss (the executables, *.dll and named
# notices), so a publish-only .pdb there never reaches a user; its text files still must not leak.
foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $outputFull "App") -File -Recurse)) {
    Assert-NoLeaks $file "App staging"
}

# The package ships as one file; check its contents through a throwaway expansion.
$expansionRoot = Join-Path ([IO.Path]::GetTempPath()) ("WSGM-PackageCheck-{0}" -f [Guid]::NewGuid().ToString("N"))
try {
foreach ($packageFile in $packageFiles) {
    $packageRoot = Get-Item -LiteralPath (New-Item -ItemType Directory -Path (Join-Path $expansionRoot $packageFile.BaseName)).FullName
    [IO.Compression.ZipFile]::ExtractToDirectory($packageFile.FullName, $packageRoot.FullName)
    foreach ($required in @("plugin.wsgm.json", "LICENSE.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageRoot.FullName $required) -PathType Leaf)) {
            throw "Plugin package $($packageFile.Name) is missing $required."
        }
    }

    $manifest = Get-Content -LiteralPath (Join-Path $packageRoot.FullName "plugin.wsgm.json") -Raw |
        ConvertFrom-Json -Depth 32
    if ($packageFile.Name -cne ("{0}-{1}.wsgmpkg" -f [string]$manifest.id, [string]$manifest.version)) {
        throw "Plugin package file name does not match its manifest identity and version: $($packageFile.Name)"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot.FullName ([string]$manifest.entryAssembly)) -PathType Leaf)) {
        throw "Plugin package $($packageFile.Name) is missing its entry assembly."
    }

    $files = @(Get-ChildItem -LiteralPath $packageRoot.FullName -File -Recurse | Sort-Object FullName)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($packageRoot.FullName, $file.FullName).Replace("\", "/")
        Assert-NoDeveloperMaterial $file "Plugin package"
        if ($relative -match '(?i)(?:^|/)(?:captures?|raw[-_]?evidence|fixtures?|recipes?)(?:/|$)') {
            throw "Plugin package contains a source-capture or evidence directory: $relative"
        }
        if ($file.Name -in @("WSGM.exe", "WSGM.Launch.exe", "WSGM.LogonService.exe",
            "WSGM.DeviceHost.exe", "wsgm-device.exe")) {
            throw "Plugin package contains an unrelated WSGM executable: $relative"
        }
        Assert-NoLeaks $file "Plugin package"
    }
}
}
finally {
    if (Test-Path -LiteralPath $expansionRoot) {
        Remove-Item -LiteralPath $expansionRoot -Recurse -Force
    }
}

Write-Host "Component isolation and package staging assertions passed."
