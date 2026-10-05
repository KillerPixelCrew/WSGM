# SPDX-License-Identifier: MIT
# Helpers shared by the MIT-licensed plugin packers (pack-device.ps1, package-plugin.ps1) and
# build-bundle.ps1: the wsgmVersion stamp and host-provided file removal.

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
        content. The names come from the Device SDK's PluginPackageLayout through plugin-manifest.cs.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory)

    $hostProvided = @(& dotnet run --file (Join-Path $PSScriptRoot 'plugin-manifest.cs') -- host-provided 2>&1)
    if ($LASTEXITCODE -ne 0 -or $hostProvided.Count -eq 0) {
        throw "Reading the host-provided assemblies from the SDK failed:`n$($hostProvided -join [Environment]::NewLine)"
    }
    $hostProvided = @($hostProvided | ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $assemblies = @(Get-ChildItem -LiteralPath $Directory -Filter '*.dll' -File | ForEach-Object { $_.BaseName })
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File)) {
        $isHostProvided = $file.BaseName -in $hostProvided -and $file.Extension -in @('.dll', '.xml', '.pdb')
        $isAssemblyCompanion = $file.Extension -in @('.xml', '.pdb') -and $file.BaseName -in $assemblies
        if ($isHostProvided -or $isAssemblyCompanion) {
            Remove-Item -LiteralPath $file.FullName -Force
        }
    }
}
