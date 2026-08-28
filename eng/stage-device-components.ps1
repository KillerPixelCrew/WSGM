<#[
.SYNOPSIS
    Publishes every JIT-only device component into an isolated release staging tree.

.DESCRIPTION
    WSGM's NativeAOT app tree is deliberately not an input or output of this script. DeviceHost,
    Device Lab, and the command-line tools each receive their own self-contained JIT output;
    plugin packages remain managed libraries loaded into DeviceHost. A plugin package is assembled from its compiled output plus only
    the reviewed manifest/provenance files named below; source captures and build diagnostics never
    enter the package.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [string]$Version = "1.0.0",

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
        "/p:PublishAot=false",
        "/p:PublishSingleFile=false",
        "/p:TreatWarningsAsErrors=true"
    )
    if ($NoRestore) { $arguments += "--no-restore" }
    if ($PlatformX64) { $arguments += "/p:PlatformTarget=x64" }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $Project failed."
    }
}

function Write-PackageHashRecord(
    [string]$PackageRoot,
    [string]$PackageId,
    [string]$PackageVersion
) {
    $recordPath = Join-Path $PackageRoot "package-files.wsgm.json"
    $files = @(
        Get-ChildItem -LiteralPath $PackageRoot -File -Recurse |
        Where-Object { $_.FullName -cne $recordPath } |
        ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($PackageRoot, $_.FullName).Replace("\", "/")
                length = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        } |
        Sort-Object { $_.path }
    )
    $record = [ordered]@{
        schemaVersion = 1
        packageId = $PackageId
        packageVersion = $PackageVersion
        configuration = $Configuration
        runtimeIdentifier = $RuntimeIdentifier
        files = $files
    }
    $json = $record | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($recordPath, $json + "`n", [Text.UTF8Encoding]::new($false))
}

function Assert-RegularSourceFile([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Package metadata may not be copied through a link or reparse point: $Path"
    }
}

try {
    $hostDestination = Join-Path $temporaryRoot "DeviceHost"
    Invoke-ComponentPublish `
        "src\WSGM.DeviceHost\WSGM.DeviceHost.csproj" $hostDestination $Version

    $deviceLabDestination = Join-Path $temporaryRoot "Tools\DeviceLab"
    Invoke-ComponentPublish `
        "src\WSGM.DeviceLab.Gui\WSGM.DeviceLab.Gui.csproj" $deviceLabDestination $Version

    $commandLineDestination = Join-Path $temporaryRoot "Tools\CommandLine"
    Invoke-ComponentPublish `
        "src\WSGM.DeviceLab.Cli\WSGM.DeviceLab.Cli.csproj" $commandLineDestination $Version

    $probeHostDestination = Join-Path $temporaryRoot "Tools\ProbeHost"
    Invoke-ComponentPublish `
        "src\WSGM.Device.ProbeHost\WSGM.Device.ProbeHost.csproj" $probeHostDestination $Version

    $pluginManifests = @(
        Get-ChildItem -LiteralPath (Join-Path $root "plugins") `
            -Filter "plugin.wsgm.json" -File -Recurse |
        Sort-Object FullName
    )
    if ($pluginManifests.Count -eq 0) {
        throw "No reviewed plugin.wsgm.json files were found."
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
        $entryPoint = [string]$manifest.entryPoint
        $safeSegment = '^[A-Za-z0-9._-]+$'
        if ($packageId -notmatch $safeSegment -or $packageVersion -notmatch '^[0-9]+(?:\.[0-9]+){1,3}$') {
            throw "$($manifestFile.FullName) has an unsafe package id or version."
        }
        if ([IO.Path]::IsPathRooted($entryPoint) -or [IO.Path]::GetFileName($entryPoint) -cne $entryPoint) {
            throw "$($manifestFile.FullName) must name a package-root entry assembly."
        }

        $packageDestination = Join-Path $temporaryRoot "Packages\$packageId\$packageVersion"
        Invoke-ComponentPublish `
            ([IO.Path]::GetRelativePath($root, $projectFiles[0].FullName)) `
            $packageDestination $packageVersion -PlatformX64 -FrameworkDependent

        Get-ChildItem -LiteralPath $packageDestination -Filter "*.pdb" -File -Recurse |
            Remove-Item -Force
        Copy-Item -LiteralPath $manifestFile.FullName `
            -Destination (Join-Path $packageDestination "plugin.wsgm.json")

        foreach ($metadataName in @("evidence.lock.json", "PROVENANCE.md", "THIRD_PARTY_NOTICES.md")) {
            $metadataPath = Join-Path $sourceDirectory $metadataName
            if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
                Assert-RegularSourceFile $metadataPath
                Copy-Item -LiteralPath $metadataPath -Destination $packageDestination
            }
        }

        $licenseNoticePath = [string]$manifest.provenance.licenseNoticePath
        if (-not [string]::IsNullOrWhiteSpace($licenseNoticePath)) {
            if ([IO.Path]::IsPathRooted($licenseNoticePath) -or
                $licenseNoticePath.Replace("\", "/").Split('/') -contains "..") {
                throw "$($manifestFile.FullName) has an unsafe license notice path."
            }
            $licenseSource = Join-Path $sourceDirectory $licenseNoticePath
            if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf) -and
                $licenseNoticePath -ceq "LICENSE.txt") {
                $licenseSource = Join-Path $root "LICENSE"
            }
            if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) {
                throw "License notice '$licenseNoticePath' is missing for $packageId."
            }
            Assert-RegularSourceFile $licenseSource
            $licenseDestination = Join-Path $packageDestination $licenseNoticePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $licenseDestination) -Force | Out-Null
            Copy-Item -LiteralPath $licenseSource -Destination $licenseDestination
        }

        if (-not (Test-Path -LiteralPath (Join-Path $packageDestination $entryPoint) -PathType Leaf)) {
            throw "Published package $packageId did not produce entry point $entryPoint."
        }
        Write-PackageHashRecord $packageDestination $packageId $packageVersion

        # Run the product's own bounded offline validator against the exact bytes
        # that will be handed to signing/installer work. This never loads plugin
        # code, starts DeviceHost, probes hardware, or grants package trust.
        $validator = Join-Path $commandLineDestination "wsgm-device.exe"
        $validationOutput = @(& $validator validate offline $packageDestination 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "Offline package validation failed for $packageId`: $($validationOutput -join [Environment]::NewLine)"
        }
    }

    foreach ($component in @("DeviceHost", "Tools", "Packages")) {
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

Write-Host "JIT device components staged under $outputFull."
