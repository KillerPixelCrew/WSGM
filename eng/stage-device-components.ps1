<#[
.SYNOPSIS
    Publishes Device Lab and the built-in device plugin into release staging.

.DESCRIPTION
    Device Lab receives a separate self-contained output with GUI and CLI modes. Plugin packages
    remain managed libraries loaded by WSGM. The one package is assembled
    from its compiled output plus only the manifest and license notices named below; source captures and build diagnostics never
    enter the package.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [string]$Version = "1.0.0",

    [string]$BuiltInPackageId = "wsgm.device.msi.claw-8-a2vm",

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
$repositoryFull = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) `
    + [IO.Path]::DirectorySeparatorChar
if (-not ($outputFull + [IO.Path]::DirectorySeparatorChar).StartsWith(
    $repositoryFull,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Device component staging must stay inside the repository workspace."
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "WSGM-DeviceComponents-{0}-{1}" -f $PID, [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$acquisitionMetadata = @(
    ".pinned-version",
    ".pinned-payload.json",
    ".wsgm-acquisition-owner.json"
)

function Invoke-ComponentPublish(
    [string]$Project,
    [string]$Destination,
    [string]$ComponentVersion,
    [switch]$PlatformX64,
    [switch]$FrameworkDependent
) {
    $arguments = @(
        "publish",
        (Join-Path $root $Project),
        "--configuration", $Configuration,
        "--runtime", $RuntimeIdentifier,
        "--self-contained", $(if ($FrameworkDependent) { "false" } else { "true" }),
        "--output", $Destination,
        "/p:Version=$ComponentVersion",
        "/p:PublishSingleFile=false",
        "/p:TreatWarningsAsErrors=true",
        "-m:1"
    )
    if ($NoRestore) { $arguments += "--no-restore" }
    if ($PlatformX64) { $arguments += "/p:PlatformTarget=x64" }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $Project failed."
    }
}

function Assert-RegularSourceFile([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Package metadata may not be copied through a link or reparse point: $Path"
    }
}

function Copy-DotNetRuntimeNotices(
    [string]$AssetsPath,
    [string]$Destination
) {
    if (-not (Test-Path -LiteralPath $AssetsPath -PathType Leaf)) {
        throw "Component restore assets are missing: $AssetsPath"
    }

    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -Depth 100
    $runtimePackName = "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier"
    $frameworks = @($assets.project.frameworks.psobject.Properties | ForEach-Object { $_.Value })
    $runtimeDependencies = @(
        $frameworks |
            ForEach-Object { $_.downloadDependencies } |
            Where-Object { [string]$_.name -ieq $runtimePackName }
    )
    if ($runtimeDependencies.Count -ne 1) {
        throw "Component restore must resolve exactly one $runtimePackName pack."
    }

    $versionRange = ([string]$runtimeDependencies[0].version -replace '^\[|\]$', '')
    $bounds = @($versionRange.Split(',') | ForEach-Object { $_.Trim() })
    if ($bounds.Count -ne 2 -or $bounds[0] -cne $bounds[1] -or
        [string]::IsNullOrWhiteSpace($bounds[0])) {
        throw "Component runtime pack version is not exact: $($runtimeDependencies[0].version)"
    }

    $runtimePack = $null
    foreach ($packageFolder in $assets.packageFolders.psobject.Properties.Name) {
        $candidate = Join-Path `
            (Join-Path $packageFolder ($runtimePackName.ToLowerInvariant())) `
            $bounds[0]
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $runtimePack = $candidate
            break
        }
    }
    if ($null -eq $runtimePack) {
        throw "Resolved component runtime pack was not found in the restored package folders."
    }

    foreach ($notice in @(
        @{ Source = "LICENSE.TXT"; Destination = "DotNetRuntime-LICENSE.txt" },
        @{ Source = "THIRD-PARTY-NOTICES.TXT"; Destination = "DotNetRuntime-THIRD-PARTY-NOTICES.txt" }
    )) {
        $source = Join-Path $runtimePack $notice.Source
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required .NET runtime notice is missing: $source"
        }
        Assert-RegularSourceFile $source
        Copy-Item -LiteralPath $source `
            -Destination (Join-Path $Destination $notice.Destination) -Force
    }
}

try {
    # Device Lab is built in its own repository and pinned here by digest, not compiled from this
    # tree. It references the plugin SDK as its own submodule, so building it inside this solution
    # would put two WSGM.Device.Sdk projects in one build, from two pins that can drift apart.
    # The acquired tree is already self-contained and already carries its own licence notices.
    $deviceLabDestination = Join-Path $temporaryRoot "Tools\DeviceLab"
    $deviceLabStaging = & (Join-Path $PSScriptRoot "acquire-devicelab.ps1")
    New-Item -ItemType Directory -Path $deviceLabDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $deviceLabStaging "*") `
        -Destination $deviceLabDestination -Recurse -Force
    # Acquisition ownership and cache-validation metadata belong to the local cache, not to the
    # released component payload.
    foreach ($metadataName in $acquisitionMetadata) {
        $metadataPath = Join-Path $deviceLabDestination $metadataName
        if (Test-Path -LiteralPath $metadataPath) {
            Remove-Item -LiteralPath $metadataPath -Force
        }
    }

    # The built-in device package is built, validated and packed in its own repository and pinned
    # here by digest. It references the plugin SDK as its own submodule, so building it inside this
    # solution would put two WSGM.Device.Sdk projects in one build, from two pins free to drift.
    # The acquired tree already carries its manifest, glyph artwork, licence and notices.
    $pluginStaging = & (Join-Path $PSScriptRoot "acquire-claw-plugin.ps1")
    $lockedPlugin = Get-Content -LiteralPath `
        (Join-Path $root "third_party\claw-plugin\claw-plugin.lock.json") -Raw | ConvertFrom-Json

    $manifestFile = Join-Path $pluginStaging "plugin.wsgm.json"
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json -Depth 32
    $packageId = [string]$manifest.id
    $entryAssembly = [string]$manifest.entryAssembly
    if ($packageId -cne $BuiltInPackageId) {
        throw "The pinned package declares id '$packageId', not the expected '$BuiltInPackageId'."
    }
    $safeSegment = '^[A-Za-z0-9._-]+$'
    if ($packageId -notmatch $safeSegment -or
        [string]$manifest.version -notmatch '^[0-9]+(?:\.[0-9]+){1,3}$') {
        throw "$manifestFile has an unsafe package id or version."
    }
    if ([IO.Path]::IsPathRooted($entryAssembly) -or
        [IO.Path]::GetFileName($entryAssembly) -cne $entryAssembly -or
        [IO.Path]::GetExtension($entryAssembly) -cne '.dll') {
        throw "$manifestFile must name a package-root entry assembly."
    }

    $packageDestination = Join-Path $temporaryRoot "Packages\$packageId"
    New-Item -ItemType Directory -Path $packageDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $pluginStaging "*") `
        -Destination $packageDestination -Recurse -Force
    # Acquisition ownership and cache-validation metadata belong to the local cache, not to the
    # released package.
    foreach ($metadataName in $acquisitionMetadata) {
        $metadataPath = Join-Path $packageDestination $metadataName
        if (Test-Path -LiteralPath $metadataPath) {
            Remove-Item -LiteralPath $metadataPath -Force
        }
    }

    foreach ($required in @("PROVENANCE.md", "THIRD_PARTY_NOTICES.md", "LICENSE.txt", $entryAssembly)) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageDestination $required) -PathType Leaf)) {
            throw "The pinned package is missing required content: $required"
        }
    }

    # Package validation treats glyph artwork as optional, so a package that lost it still passes
    # every other gate and simply ships without physical glyphs. The lock file records that this
    # package is known to carry them, which turns a silent feature loss into a build failure.
    $expectedGlyphFiles = [int]$lockedPlugin.component.glyphFiles
    if ($expectedGlyphFiles -gt 0) {
        $stagedGlyphs = @(Get-ChildItem -LiteralPath (Join-Path $packageDestination "glyphs") `
            -File -Recurse -ErrorAction SilentlyContinue)
        if ($stagedGlyphs.Count -ne $expectedGlyphFiles) {
            throw ("The pinned package carries $($stagedGlyphs.Count) glyph file(s); " +
                "claw-plugin.lock.json expects $expectedGlyphFiles.")
        }
        Write-Host "  glyph assets staged: $($stagedGlyphs.Count) file(s)"
    }

    # Re-run the product's own bounded offline validator against the exact bytes that will be
    # handed to the installer. The plugin's own repository validated what it packed; this validates
    # what ships, which is the only claim this build can make. It never loads plugin code or probes
    # hardware.
    $validator = Join-Path $deviceLabDestination "wsgm-device.exe"
    $validationOutput = @(& $validator validate $packageDestination 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Offline package validation failed for $packageId`: $($validationOutput -join [Environment]::NewLine)"
    }

    foreach ($component in @("Tools", "Packages")) {
        $destination = Join-Path $outputFull $component
        if (Test-Path -LiteralPath $destination) {
            throw "Refusing to overwrite existing component staging: $destination"
        }
        $parent = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Move-Item -LiteralPath (Join-Path $temporaryRoot $component) -Destination $destination
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Device tools and package staged under $outputFull."
