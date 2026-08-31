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
    # The staging stamp records the pin for the acquire script; it is not part of the payload.
    Remove-Item -LiteralPath (Join-Path $deviceLabDestination ".pinned-version") `
        -Force -ErrorAction SilentlyContinue

    $allPluginManifests = @(
        Get-ChildItem -LiteralPath (Join-Path $root "plugins") `
            -Filter "plugin.wsgm.json" -File -Recurse |
        Where-Object { $_.FullName -notmatch "[\\/](?:bin|obj)[\\/]" } |
        Sort-Object FullName
    )
    $pluginManifests = @(
        $allPluginManifests |
            Where-Object {
                $candidateManifest = Get-Content -LiteralPath $_.FullName -Raw |
                    ConvertFrom-Json -Depth 32
                [string]$candidateManifest.id -ceq $BuiltInPackageId
            }
    )
    if ($pluginManifests.Count -ne 1) {
        throw "Release staging requires exactly one '$BuiltInPackageId' manifest; found $($pluginManifests.Count)."
    }

    foreach ($manifestFile in $pluginManifests) {
        Assert-RegularSourceFile $manifestFile.FullName
        $sourceDirectory = $manifestFile.Directory.FullName
        $projectFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter "*.csproj" -File)
        if ($projectFiles.Count -ne 1) {
            throw "$sourceDirectory must contain exactly one plugin project."
        }

        $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json -Depth 32
        $packageId = [string]$manifest.id
        $packageVersion = [string]$manifest.version
        $entryAssembly = [string]$manifest.entryAssembly
        $safeSegment = '^[A-Za-z0-9._-]+$'
        if ($packageId -notmatch $safeSegment -or $packageVersion -notmatch '^[0-9]+(?:\.[0-9]+){1,3}$') {
            throw "$($manifestFile.FullName) has an unsafe package id or version."
        }
        if ([IO.Path]::IsPathRooted($entryAssembly) -or
            [IO.Path]::GetFileName($entryAssembly) -cne $entryAssembly -or
            [IO.Path]::GetExtension($entryAssembly) -cne '.dll') {
            throw "$($manifestFile.FullName) must name a package-root entry assembly."
        }

        $packageDestination = Join-Path $temporaryRoot "Packages\$packageId"
        Invoke-ComponentPublish `
            ([IO.Path]::GetRelativePath($root, $projectFiles[0].FullName)) `
            $packageDestination $packageVersion -PlatformX64 -FrameworkDependent

        Get-ChildItem -LiteralPath $packageDestination -Filter "*.pdb" -File -Recurse |
            Remove-Item -Force
        Copy-Item -LiteralPath $manifestFile.FullName `
            -Destination (Join-Path $packageDestination "plugin.wsgm.json")

        foreach ($metadataName in @("PROVENANCE.md", "THIRD_PARTY_NOTICES.md")) {
            $metadataPath = Join-Path $sourceDirectory $metadataName
            if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
                throw "Required built-in package notice is missing: $metadataPath"
            }
            Assert-RegularSourceFile $metadataPath
            Copy-Item -LiteralPath $metadataPath -Destination $packageDestination
        }

        # Physical glyph artwork, if the package ships any. The importer discovers profiles purely
        # by directory (glyphs/profiles/*.json, glyphs/assets/<sha256>.<ext>), so the layout is
        # copied through verbatim; every file is checked for link redirection the same way the
        # manifest and notices are, because these are read from the installed package at runtime.
        $glyphSource = Join-Path $sourceDirectory "glyphs"
        if (Test-Path -LiteralPath $glyphSource -PathType Container) {
            $glyphFiles = @(Get-ChildItem -LiteralPath $glyphSource -File -Recurse)
            foreach ($glyphFile in $glyphFiles) {
                Assert-RegularSourceFile $glyphFile.FullName
                $relative = [IO.Path]::GetRelativePath($sourceDirectory, $glyphFile.FullName)
                $target = Join-Path $packageDestination $relative
                $targetParent = Split-Path -Parent $target
                if (-not (Test-Path -LiteralPath $targetParent -PathType Container)) {
                    New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
                }
                Copy-Item -LiteralPath $glyphFile.FullName -Destination $target -Force
            }
            Write-Host "  glyph assets staged: $($glyphFiles.Count) file(s)"
        }

        # The built-in package is first-party GPL code. Materialize the package copy
        # from the repository's authoritative license instead of maintaining a duplicate.
        $licensePath = Join-Path $root "LICENSE"
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
            throw "Repository GPL license is missing: $licensePath"
        }
        Assert-RegularSourceFile $licensePath
        Copy-Item -LiteralPath $licensePath `
            -Destination (Join-Path $packageDestination "LICENSE.txt") -Force

        if (-not (Test-Path -LiteralPath (Join-Path $packageDestination $entryAssembly) -PathType Leaf)) {
            throw "Published package $packageId did not produce entry assembly $entryAssembly."
        }
        # Run the product's own bounded offline validator against the exact bytes
        # that will be handed to the installer. This never loads plugin code,
        # loads plugin code or probes hardware.
        $validator = Join-Path $deviceLabDestination "wsgm-device.exe"
        $validationOutput = @(& $validator validate $packageDestination 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "Offline package validation failed for $packageId`: $($validationOutput -join [Environment]::NewLine)"
        }
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
