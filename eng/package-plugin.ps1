# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
Builds a common plugin into a .wsgmpkg without loading or installing it.
.DESCRIPTION
Uses a new staging directory and create-new archive publication. The manifest is validated by this
checkout's common Plugin SDK through plugin-manifest.cs, the reader the host uses at discovery. This
command then checks the common category, entry file and package contents before creating the
archive. WSGM loads the package straight from the file, so the plugin is published for win-x64,
which puts every dependency at the package root, and a native image is refused because it cannot be
loaded from memory. Install it by copying the file into %ProgramFiles%\WSGM\Plugins.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Archive,
    [ValidatePattern('^$|^[0-9]+(\.[0-9]+){1,3}$')][string]$WsgmVersion = '',
    [string[]]$MsBuildArgument = @()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'device-package-output.ps1')
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$archivePath = [IO.Path]::GetFullPath($Archive)
if ([IO.Path]::GetExtension($archivePath) -ne '.wsgmpkg') { throw 'The archive must use the .wsgmpkg extension.' }
if (Test-Path -LiteralPath $archivePath) { throw 'Archive already exists; choose a new output name.' }
$parent = [IO.Directory]::GetParent($archivePath)
if ($null -eq $parent -or -not $parent.Exists) { throw 'Archive parent must exist.' }
for ($ancestor = $parent; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Archive output cannot traverse a reparse point.' }
}
$stage = Join-Path $parent.FullName ('.wsgm-plugin-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
$payload = Join-Path $stage 'payload'
try {
    & dotnet publish $projectPath -c Release -r win-x64 --no-self-contained -o $payload @MsBuildArgument
    if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed ($LASTEXITCODE)." }
    Remove-HostProvidedFiles -Directory $payload
    $manifestPath = Join-Path $payload 'plugin.wsgm.json'
    if ([string]::IsNullOrWhiteSpace($WsgmVersion)) { $WsgmVersion = Get-WsgmVersion -Root (Split-Path -Parent $PSScriptRoot) }
    Set-PackageWsgmVersion -ManifestPath $manifestPath -WsgmVersion $WsgmVersion
    $validation = @(& dotnet run --file (Join-Path $PSScriptRoot 'plugin-manifest.cs') -- validate $manifestPath 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "The Plugin SDK rejected the manifest:`n$($validation -join [Environment]::NewLine)" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.category -eq 'wsgm.device') { throw 'Invalid common plugin identity or category.' }
    if (-not (Test-Path -LiteralPath (Join-Path $payload $manifest.entryAssembly) -PathType Leaf)) {
        throw 'Entry assembly must exist at the package root.'
    }
    $files = @(Get-ChildItem -LiteralPath $payload -Recurse -Force)
    if ($files.Count -gt 4096 -or @($files | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Package contains too many entries or a reparse point.'
    }
    foreach ($image in @($files | Where-Object { $_.Extension -in '.dll', '.exe', '.sys' })) {
        $stream = [IO.File]::OpenRead($image.FullName)
        try {
            $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
            if ($null -eq $pe.PEHeaders.CorHeader) { throw "Packages may carry managed assemblies only: $($image.Name) is native." }
        }
        finally { $stream.Dispose() }
    }
    $stagedArchive = Join-Path $stage 'package.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $stagedArchive)
    Publish-DevicePackageArchive -StagedArchive $stagedArchive -Archive $archivePath
    Write-Output "Created $archivePath for $($manifest.id) $($manifest.version). No plugin code was loaded."
}
finally {
    # This exact, newly created directory is owned by this invocation and stays below the checked parent.
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ([IO.Directory]::GetParent($resolvedStage).FullName -ne $parent.FullName -or
        [IO.Path]::GetFileName($resolvedStage) -notlike '.wsgm-plugin-*') { throw 'Refusing unexpected staging cleanup path.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
