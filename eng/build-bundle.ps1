<#
.SYNOPSIS
    Builds every bundled plugin package, the bundle manifest and Device Lab from this checkout.

.DESCRIPTION
    Setup carries every accepted plugin and installs only the device plugin whose hardware rules
    match the machine, so a release needs each package plus one bundle.json describing them. The
    list of plugins is plugins\curated\*.json, the only place their origin and validation status
    are set.

    First-party plugins are packed from this checkout: device packages through pack-device.ps1,
    which validates them with the Device Lab built here, and common packages through
    package-plugin.ps1. A community plugin is built from the exact commit its curated file pins,
    against this checkout's SDK packed into a local feed. A pinned commit that no longer builds is
    recorded in bundle.json as outdated instead of failing the release; -SkipCommunity leaves
    community plugins out entirely, for offline local builds.

    Run the community step only in a job that holds no secrets: it builds and therefore executes
    third-party MSBuild code.

    Output under -OutputRoot: Packages\<id>-<version>.wsgmpkg, bundle.json, and Tools\DeviceLab
    unless -SkipTools. Nothing touches hardware.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [switch]$NoRestore,

    [switch]$SkipCommunity,

    [switch]$SkipTools,

    # Builds only these plugin ids, for a development deploy.
    [string[]]$Only = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "device-lab-publish.ps1")
. (Join-Path $PSScriptRoot "device-package-output.ps1")
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
$repositoryFull = [IO.Path]::GetFullPath($root).TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not ($outputFull + [IO.Path]::DirectorySeparatorChar).StartsWith(
        $repositoryFull,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Bundle output must stay inside the repository workspace."
}

$wsgmVersion = Get-WsgmVersion -Root $root
$curatedRoot = Join-Path $root "plugins\curated"
$packDevice = Join-Path $PSScriptRoot "pack-device.ps1"
$packCommon = Join-Path $PSScriptRoot "package-plugin.ps1"

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "WSGM-Bundle-{0}-{1}" -f $PID, [Guid]::NewGuid().ToString("N"))
$temporaryMarker = Join-Path $temporaryRoot ".wsgm-bundle-stage"
$temporaryMarkerValue = "WSGM bundle stage v1"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
Set-Content -LiteralPath $temporaryMarker -Value $temporaryMarkerValue -NoNewline

function Read-Manifest([string]$Path) {
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 32
}

function Build-FirstPartyPackage([string]$ProjectDirectory, [string]$Validator, [string]$Destination,
    [string[]]$MsBuildArgument = @()) {
    $manifest = Read-Manifest (Join-Path $ProjectDirectory "plugin.wsgm.json")
    $archive = Join-Path $Destination ("{0}-{1}.wsgmpkg" -f $manifest.id, $manifest.version)
    if ($manifest.PSObject.Properties.Name -contains "category") {
        $projects = @(Get-ChildItem -LiteralPath $ProjectDirectory -Filter "*.csproj" -File)
        if ($projects.Count -ne 1) {
            throw "Expected exactly one project in $ProjectDirectory."
        }
        & $packCommon -Project $projects[0].FullName -Archive $archive -WsgmVersion $wsgmVersion `
            -MsBuildArgument $MsBuildArgument | Out-Host
    }
    else {
        $packArguments = @{
            Source = $ProjectDirectory
            RequireGlyphs = (Test-Path -LiteralPath (Join-Path $ProjectDirectory "glyphs") -PathType Container)
            OutputRoot = $Destination
            Configuration = $Configuration
            RuntimeIdentifier = $RuntimeIdentifier
            DeviceLabExecutable = $Validator
            WsgmVersion = $wsgmVersion
            MsBuildArgument = $MsBuildArgument
        }
        if ($NoRestore -and $MsBuildArgument.Count -eq 0) {
            $packArguments.NoRestore = $true
        }
        & $packDevice @packArguments | Out-Host
    }
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "Packing $($manifest.id) did not produce $archive."
    }
    return $archive
}

$sdkFeed = $null
function Get-SdkFeed {
    if ($null -ne $script:sdkFeed) {
        return $script:sdkFeed
    }
    $feed = Join-Path $temporaryRoot "sdk-feed"
    foreach ($sdk in @("src\WSGM.Device.Sdk\WSGM.Device.Sdk.csproj", "src\WSGM.Plugin.Sdk\WSGM.Plugin.Sdk.csproj")) {
        & dotnet pack (Join-Path $root $sdk) --configuration $Configuration --output $feed "-p:Version=$wsgmVersion" | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Packing $sdk into the local SDK feed failed."
        }
    }
    $targets = Join-Path $temporaryRoot "wsgm-sdk.targets"
    Set-Content -LiteralPath $targets -Encoding utf8 -Value @"
<Project>
  <ItemGroup>
    <PackageReference Update="WSGM.Device.Sdk" Version="$wsgmVersion" />
    <PackageReference Update="WSGM.Plugin.Sdk" Version="$wsgmVersion" />
  </ItemGroup>
</Project>
"@
    $script:sdkFeed = @("-p:RestoreAdditionalProjectSources=$feed", "-p:CustomAfterMicrosoftCommonTargets=$targets")
    return $script:sdkFeed
}

function Build-CommunityPackage($Curated, [string]$Validator, [string]$Destination) {
    $source = $Curated.source
    if ([string]$source.commit -notmatch '^[0-9a-f]{40}$') {
        throw "$($Curated.id) must pin a full commit SHA."
    }
    $clone = Join-Path $temporaryRoot ("community\" + $Curated.id)
    & git clone --quiet --no-checkout ([string]$source.repository) $clone
    if ($LASTEXITCODE -ne 0) { throw "Cloning $($source.repository) failed." }
    & git -C $clone checkout --quiet --detach ([string]$source.commit)
    if ($LASTEXITCODE -ne 0) { throw "Checking out $($source.commit) failed." }
    $head = (& git -C $clone rev-parse HEAD).Trim()
    if ($head -cne [string]$source.commit) { throw "The clone is at $head, not the pinned commit." }
    $projectDirectory = [IO.Path]::GetFullPath((Join-Path $clone ([string]$source.project)))
    if (-not $projectDirectory.StartsWith($clone, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$($Curated.id) names a project outside its repository."
    }
    return Build-FirstPartyPackage $projectDirectory $Validator $Destination (Get-SdkFeed)
}

function Get-OutdatedLog([string]$Message) {
    if ($env:GITHUB_RUN_ID) {
        return "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)"
    }
    return $Message
}

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

    $packed = Join-Path $temporaryRoot "Packed"
    $packages = Join-Path $temporaryRoot "Packages"
    New-Item -ItemType Directory -Path $packed, $packages | Out-Null
    $entries = [Collections.Generic.List[object]]::new()
    $outdated = [Collections.Generic.List[object]]::new()

    foreach ($curatedFile in @(Get-ChildItem -LiteralPath $curatedRoot -Filter "*.json" -File | Sort-Object Name)) {
        $curated = Read-Manifest $curatedFile.FullName
        if ($curated.id -cne $curatedFile.BaseName) {
            throw "$($curatedFile.Name) must be named after the id it describes."
        }
        if ($curated.origin -notin @("first-party", "community") -or
            $curated.validation -notin @("hardware-tested", "blind")) {
            throw "$($curatedFile.Name) needs origin first-party|community and validation hardware-tested|blind."
        }
        if ($curated.bundle -eq $false -or ($Only.Count -gt 0 -and $curated.id -notin $Only)) {
            continue
        }

        $contact = if ($curated.PSObject.Properties.Name -contains "contact") { [string]$curated.contact } else { $null }
        Write-Host "== Packing $($curated.id) ==" -ForegroundColor Cyan
        if ($curated.origin -eq "community") {
            if ($SkipCommunity) {
                Write-Warning "Skipping community plugin $($curated.id) (-SkipCommunity)."
                continue
            }
            try {
                $archive = Build-CommunityPackage $curated $validator $packed
            }
            catch {
                Write-Warning "Community plugin $($curated.id) is outdated for WSGM ${wsgmVersion}: $_"
                $outdated.Add([ordered]@{ id = $curated.id; contact = $contact; log = (Get-OutdatedLog "$_") })
                continue
            }
        }
        else {
            $archive = Build-FirstPartyPackage (Join-Path $root ([string]$curated.project)) $validator $packed
        }

        # Check the exact archive before it enters the bundle.
        $expanded = Join-Path $temporaryRoot ("Expanded\" + $curated.id)
        New-Item -ItemType Directory -Path $expanded | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $expanded)
        $manifest = Read-Manifest (Join-Path $expanded "plugin.wsgm.json")
        if ($manifest.id -cne $curated.id) {
            throw "The package built for $($curated.id) declares id $($manifest.id)."
        }
        if ($manifest.wsgmVersion -cne $wsgmVersion) {
            throw "$($curated.id) was not stamped for WSGM $wsgmVersion."
        }
        if (-not (Test-Path -LiteralPath (Join-Path $expanded "LICENSE.txt") -PathType Leaf)) {
            throw "$($curated.id) ships no LICENSE.txt."
        }
        $isDevice = -not ($manifest.PSObject.Properties.Name -contains "category")
        if ($isDevice) {
            $validation = @(& $validator validate $expanded 2>&1)
            if ($LASTEXITCODE -ne 0) {
                throw "Offline validation failed for $($curated.id):`n$($validation -join [Environment]::NewLine)"
            }
        }

        $file = Split-Path -Leaf $archive
        Copy-Item -LiteralPath $archive -Destination (Join-Path $packages $file)
        # Assigned through typed variables: an if-expression inside the literal would unroll a
        # one-element list into a bare object, and a missing list into null.
        [object[]]$hardware = @()
        if ($manifest.PSObject.Properties.Name -contains "hardware") { $hardware = @($manifest.hardware) }
        [object[]]$capabilities = @()
        if ($manifest.PSObject.Properties.Name -contains "capabilities") { $capabilities = @($manifest.capabilities) }
        $entries.Add([ordered]@{
            id = [string]$manifest.id
            name = [string]$manifest.name
            version = [string]$manifest.version
            category = if ($isDevice) { "wsgm.device" } else { [string]$manifest.category }
            origin = [string]$curated.origin
            validation = [string]$curated.validation
            contact = $contact
            hardware = $hardware
            capabilities = $capabilities
            file = $file
            size = (Get-Item -LiteralPath $archive).Length
            sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }

    $bundle = [ordered]@{
        schemaVersion = 1
        wsgmVersion = $wsgmVersion
        plugins = @($entries)
        outdated = @($outdated)
    }
    $bundleJson = $bundle | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText((Join-Path $temporaryRoot "bundle.json"), $bundleJson + "`n", [Text.UTF8Encoding]::new($false))

    $components = @("Packages", "bundle.json")
    if (-not $SkipTools) {
        $components += "Tools"
    }
    New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
    foreach ($component in $components) {
        $destination = Join-Path $outputFull $component
        if (Test-Path -LiteralPath $destination) {
            throw "Refusing to overwrite existing bundle output: $destination"
        }
        Move-Item -LiteralPath (Join-Path $temporaryRoot $component) -Destination $destination
    }
    Write-Host ("Bundled {0} plugin(s) for WSGM {1}; {2} outdated." -f $entries.Count, $wsgmVersion, $outdated.Count)
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
            [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $markerIsValid = (Test-Path -LiteralPath $temporaryMarker -PathType Leaf) -and
            (Get-Content -LiteralPath $temporaryMarker -Raw).Trim() -ceq $temporaryMarkerValue
        if ($resolvedTemporaryRoot.StartsWith($systemTemporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedTemporaryRoot).StartsWith("WSGM-Bundle-$PID-", [StringComparison]::Ordinal) -and
            $markerIsValid) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
        else {
            Write-Warning "Refusing to remove an unrecognized bundle staging directory: $resolvedTemporaryRoot"
        }
    }
}
