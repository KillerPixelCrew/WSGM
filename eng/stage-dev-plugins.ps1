# Prepare replacements for already installed plugins before dev-deploy stops the live session.
function Get-DevPluginCopies {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$AppPublish,
        [Parameter(Mandatory)][string]$PluginsRoot,
        [string[]]$RetiredPackageIds = @()
    )

    if (-not (Test-Path -LiteralPath $PluginsRoot -PathType Container)) { return }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $appVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $AppPublish 'WSGM.dll')).FileVersion
    $release = $appVersion.ToString(3)
    $sourceVersions = @{}
    foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $Root 'src') -Directory -Filter 'WSGM.Plugin.*') {
        $manifestPath = Join-Path $directory.FullName 'plugin.wsgm.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { continue }
        $sourceManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $sourceVersions[$sourceManifest.id] = @{ Directory = $directory.FullName; Manifest = $sourceManifest }
    }

    foreach ($package in Get-ChildItem -LiteralPath $PluginsRoot -File -Filter '*.wsgmpkg') {
        if ($package.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $manifests = @($archive.Entries | Where-Object FullName -CEQ 'plugin.wsgm.json')
            if ($manifests.Count -ne 1 -or $manifests[0].Length -gt 1MB) {
                throw "Cannot inspect installed plugin $($package.Name): missing or oversized manifest."
            }
            $reader = [IO.StreamReader]::new($manifests[0].Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        } finally { $archive.Dispose() }
        if ($manifest.id -cin $RetiredPackageIds) { continue }
        $stamp = $manifest.PSObject.Properties['wsgmVersion']
        $builtFor = if ($stamp) { [string]$stamp.Value } else { '(unstamped)' }
        $packageVersion = $null
        if ([version]::TryParse($builtFor, [ref]$packageVersion) -and
            $packageVersion.Major -eq $appVersion.Major -and $packageVersion.Minor -eq $appVersion.Minor -and
            [Math]::Max($packageVersion.Build, 0) -eq $appVersion.Build) { continue }
        if (-not $sourceVersions.ContainsKey($manifest.id)) {
            throw "$($package.Name) targets WSGM $builtFor, not $release. Supply a compatible package before deploying."
        }
        $source = $sourceVersions[$manifest.id]
        if ($source.Manifest.version -cne $manifest.version) {
            throw "$($manifest.id) source version differs from the installed version. Update that package before deploying."
        }
        $project = @(Get-ChildItem -LiteralPath $source.Directory -File -Filter '*.csproj')
        if ($project.Count -ne 1) { throw "Cannot identify the project for $($manifest.id)." }
        $stage = Join-Path $Root ('publish\dev-plugins\' + [Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($stage)
        $replacement = Join-Path $stage $package.Name
        Write-Host "== Rebuilding installed $($manifest.id) for WSGM $release (was $builtFor) ==" -ForegroundColor Cyan
        & (Join-Path $Root 'eng\package-plugin.ps1') -Project $project[0].FullName -Archive $replacement -WsgmVersion $release | Out-Host
        @{ Source = $replacement; Name = $package.Name }
    }
}
