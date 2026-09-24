# SPDX-License-Identifier: MIT
# Shared publication step for the MIT-licensed device package scripts.

function Publish-DevicePackageArchive {
    <#
    .SYNOPSIS
        Commits a staged archive without deleting the previous package first.
    .DESCRIPTION
        The caller validates output ownership and stages on the destination volume. Replacement
        is atomic; create-new publication refuses a destination created by another writer.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StagedArchive,

        [Parameter(Mandatory)]
        [string]$Archive,

        [switch]$ReplaceExisting
    )

    if ($ReplaceExisting) {
        # PowerShell coerces $null to an empty string for this parameter; pass a real null instead.
        [IO.File]::Replace($StagedArchive, $Archive, [NullString]::Value)
    }
    else {
        [IO.File]::Move($StagedArchive, $Archive)
    }
}

function Get-WsgmVersion {
    <#
    .SYNOPSIS
        Returns the WSGM release version a package is built for, from src\WSGM\WSGM.csproj.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root)

    $project = [xml](Get-Content -LiteralPath (Join-Path $Root 'src\WSGM\WSGM.csproj') -Raw)
    $version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if ([string]$version -notmatch '^[0-9]+(?:\.[0-9]+){1,3}$') {
        throw "src\WSGM\WSGM.csproj has no usable <Version>."
    }
    return [string]$version
}

function Set-PackageWsgmVersion {
    <#
    .SYNOPSIS
        Stamps the WSGM version into a staged package's manifest. WSGM refuses a package built for
        another version, and a source manifest never carries the field.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$WsgmVersion
    )

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -Depth 32
    $manifest | Add-Member -NotePropertyName 'wsgmVersion' -NotePropertyValue $WsgmVersion -Force
    $json = $manifest | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText($ManifestPath, $json + "`n", [Text.UTF8Encoding]::new($false))
}

function Remove-HostProvidedFiles {
    <#
    .SYNOPSIS
        Deletes from a staged package the assemblies WSGM always supplies itself.
    .DESCRIPTION
        WSGM answers these from its own copy whatever the package carries (PluginLoadContext.HostOwned
        and host-first resolution), so a packaged copy is never loaded. The Windows SDK projection
        alone is 24 MB. Symbols and XML documentation of every assembly go too: they are not package
        content.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory)

    $hostProvided = @('WSGM.Device.Sdk', 'WSGM.Plugin.Sdk', 'SteamUiToolkit', 'WinRT.Runtime',
        'Microsoft.Windows.SDK.NET')
    $assemblies = @(Get-ChildItem -LiteralPath $Directory -Filter '*.dll' -File | ForEach-Object { $_.BaseName })
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File)) {
        $isHostProvided = $file.BaseName -in $hostProvided -and $file.Extension -in @('.dll', '.xml', '.pdb')
        $isAssemblyCompanion = $file.Extension -in @('.xml', '.pdb') -and $file.BaseName -in $assemblies
        if ($isHostProvided -or $isAssemblyCompanion) {
            Remove-Item -LiteralPath $file.FullName -Force
        }
    }
}
