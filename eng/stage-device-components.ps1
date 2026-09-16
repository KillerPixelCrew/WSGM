<#
.SYNOPSIS
    Builds Device Lab and the built-in device plugin from this WSGM checkout.

.DESCRIPTION
    Device Lab is published self-contained for the optional tools component. The plugin packer
    assembles, validates, and packs its framework-dependent package with that exact Device Lab
    build; WSGM then expands and validates the exact package tree handed to the installer.

    All device projects share the SDK source in this repository. This script performs no downloads and
    no hardware access.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [string]$BuiltInPackageId = "wsgm.device.msi.claw-8-a2vm",

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "device-lab-publish.ps1")
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
$repositoryFull = [IO.Path]::GetFullPath($root).TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ($outputFull + [IO.Path]::DirectorySeparatorChar).StartsWith(
        $repositoryFull,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Device component staging must stay inside the repository workspace."
}

$deviceLabRoot = Join-Path $root "src\WSGM.DeviceLab"
$deviceLabProject = Join-Path $deviceLabRoot "WSGM.DeviceLab.csproj"
$pluginRoot = Join-Path $root "src\WSGM.Device.Msi.Claw8A2Vm"
$pluginPack = Join-Path $root "eng\pack-device.ps1"
$pluginSource = $pluginRoot
$manifestFile = Join-Path $pluginSource "plugin.wsgm.json"

foreach ($requiredSource in @($deviceLabProject, $pluginPack, $manifestFile)) {
    if (-not (Test-Path -LiteralPath $requiredSource -PathType Leaf)) {
        throw "A required device project source is missing: $requiredSource."
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "WSGM-DeviceComponents-{0}-{1}" -f $PID, [Guid]::NewGuid().ToString("N"))
$temporaryMarker = Join-Path $temporaryRoot ".wsgm-device-component-stage"
$temporaryMarkerValue = "WSGM device component stage v1"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
Set-Content -LiteralPath $temporaryMarker -Value $temporaryMarkerValue -NoNewline

try {
    $deviceLabDestination = Join-Path $temporaryRoot "Tools\DeviceLab"
    Publish-DeviceLab -Root $root -Destination $deviceLabDestination -Configuration $Configuration `
        -RuntimeIdentifier $RuntimeIdentifier -NoRestore:$NoRestore

    $validator = Join-Path $deviceLabDestination "wsgm-device.exe"
    foreach ($requiredToolFile in @(
        $validator,
        (Join-Path $deviceLabDestination "THIRD_PARTY_NOTICES.md"),
        (Join-Path $deviceLabDestination "DotNetRuntime-LICENSE.txt"),
        (Join-Path $deviceLabDestination "DotNetRuntime-THIRD-PARTY-NOTICES.txt")
    )) {
        if (-not (Test-Path -LiteralPath $requiredToolFile -PathType Leaf)) {
            throw "Device Lab publish is missing required content: $requiredToolFile"
        }
    }

    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json -Depth 32
    $packageId = [string]$manifest.id
    $packageVersion = [string]$manifest.version
    $entryAssembly = [string]$manifest.entryAssembly
    if ($packageId -cne $BuiltInPackageId) {
        throw "The built-in package declares id '$packageId', not the expected '$BuiltInPackageId'."
    }
    if ([IO.Path]::IsPathRooted($entryAssembly) -or
        [IO.Path]::GetFileName($entryAssembly) -cne $entryAssembly -or
        [IO.Path]::GetExtension($entryAssembly) -cne ".dll") {
        throw "$manifestFile must name a package-root entry assembly."
    }

    $packageBuildRoot = Join-Path $temporaryRoot "Packed"
    $packArguments = @{
        Source = $pluginSource
        RequireGlyphs = $true
        OutputRoot = $packageBuildRoot
        Configuration = $Configuration
        RuntimeIdentifier = $RuntimeIdentifier
        DeviceLabExecutable = $validator
    }
    if ($NoRestore) {
        $packArguments.NoRestore = $true
    }
    # pack-device.ps1 reports failure by throwing, which stops this script too. $LASTEXITCODE is no
    # status here: a script call does not set it, so it would only repeat the last native command
    # run inside the packer. A packer that returns without its archive is caught just below.
    & $pluginPack @packArguments

    $archive = Join-Path $packageBuildRoot "$packageId-$packageVersion.wsgmpkg"
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "The plugin packer did not produce the expected archive: $archive"
    }

    $archiveEntries = @(& tar -tf $archive)
    if ($LASTEXITCODE -ne 0) {
        throw "Reading the built-in package archive failed."
    }
    foreach ($archiveEntry in $archiveEntries) {
        $normalized = ([string]$archiveEntry).Replace('\', '/').TrimEnd('/')
        if ($normalized.Length -eq 0) {
            continue
        }
        $segments = @($normalized.Split('/', [StringSplitOptions]::RemoveEmptyEntries))
        if ([IO.Path]::IsPathRooted($normalized) -or
            $normalized -match '^[A-Za-z]:' -or
            $segments -contains '..') {
            throw "The built-in package archive contains an unsafe path: $archiveEntry"
        }
    }

    $packageDestination = Join-Path $temporaryRoot "Packages\$packageId"
    New-Item -ItemType Directory -Path $packageDestination -Force | Out-Null
    & tar -xf $archive -C $packageDestination
    if ($LASTEXITCODE -ne 0) {
        throw "Extracting the built-in package failed."
    }

    foreach ($required in @("PROVENANCE.md", "THIRD_PARTY_NOTICES.md", "LICENSE.txt", $entryAssembly)) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageDestination $required) -PathType Leaf)) {
            throw "The built-in package is missing required content: $required"
        }
    }

    $sourceGlyphs = @(Get-ChildItem -LiteralPath (Join-Path $pluginSource "glyphs") -File -Recurse)
    $stagedGlyphs = @(Get-ChildItem -LiteralPath (Join-Path $packageDestination "glyphs") -File -Recurse)
    if ($sourceGlyphs.Count -eq 0 -or $stagedGlyphs.Count -ne $sourceGlyphs.Count) {
        throw "The built-in package staged $($stagedGlyphs.Count) of $($sourceGlyphs.Count) source glyph files."
    }
    Write-Host "  glyph assets staged: $($stagedGlyphs.Count) file(s)"

    $validationOutput = @(& $validator validate $packageDestination 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw ("Offline package validation failed for {0}: {1}" -f
            $packageId, ($validationOutput -join [Environment]::NewLine))
    }

    foreach ($component in @("Tools", "Packages")) {
        $destination = Join-Path $outputFull $component
        if (Test-Path -LiteralPath $destination) {
            throw "Refusing to overwrite existing component staging: $destination"
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Move-Item -LiteralPath (Join-Path $temporaryRoot $component) -Destination $destination
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
            [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $markerIsValid = (Test-Path -LiteralPath $temporaryMarker -PathType Leaf) -and
            (Get-Content -LiteralPath $temporaryMarker -Raw).Trim() -cne "" -and
            (Get-Content -LiteralPath $temporaryMarker -Raw).Trim() -ceq $temporaryMarkerValue
        if ($resolvedTemporaryRoot.StartsWith(
                $systemTemporaryRoot,
                [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedTemporaryRoot).StartsWith(
                "WSGM-DeviceComponents-$PID-",
                [StringComparison]::Ordinal) -and
            $markerIsValid) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
        else {
            Write-Warning "Refusing to remove an unrecognized component staging directory: $resolvedTemporaryRoot"
        }
    }
}

Write-Host "Device tools and package staged under $outputFull."
