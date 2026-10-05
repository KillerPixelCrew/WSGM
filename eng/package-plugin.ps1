# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
Builds a common plugin into a .wsgmpkg without loading or installing it.
.DESCRIPTION
Works in a fresh temporary directory and moves the finished archive into place, refusing to replace
an existing file. The manifest is validated by this checkout's common Plugin SDK through
plugin-manifest.cs, the reader the host uses at discovery. This
command then checks the common category and entry file, creates the archive and validates it with
the Device SDK's package layout (plugin-manifest.cs validate-package), the rules WSGM applies when it
opens the package. WSGM loads the package straight from the file, so the plugin is published for
win-x64, which puts every dependency at the package root, and a native image is refused because it
cannot be loaded from memory. Install it by copying the file into %ProgramFiles%\WSGM\Plugins.
A graphics package (wsgm.gpu) is a common package too; the SDK refuses one that declares no display
adapter or no capability. It calls its vendor's driver library from the system, never a packaged copy.
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
. (Join-Path $PSScriptRoot 'plugin-package-common.ps1')
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$archivePath = [IO.Path]::GetFullPath($Archive)
if ([IO.Path]::GetExtension($archivePath) -ne '.wsgmpkg') { throw 'The archive must use the .wsgmpkg extension.' }
if (Test-Path -LiteralPath $archivePath) { throw 'Archive already exists; choose a new output name.' }
$parent = [IO.Directory]::GetParent($archivePath)
if ($null -eq $parent -or -not $parent.Exists) { throw 'Archive parent must exist.' }
$stage = Join-Path ([IO.Path]::GetTempPath()) ('WSGM-Plugin-' + [Guid]::NewGuid().ToString('N'))
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
    # CreateFromDirectory would follow a link into the archive.
    $files = @(Get-ChildItem -LiteralPath $payload -Recurse -Force)
    if (@($files | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Package contains a reparse point.'
    }
    $stagedArchive = Join-Path $stage 'package.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $stagedArchive)
    # The archive WSGM will open, checked by the SDK's package layout as WSGM checks it.
    $packageValidation = @(& dotnet run --file (Join-Path $PSScriptRoot 'plugin-manifest.cs') -- validate-package $stagedArchive 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "WSGM would refuse the package:`n$($packageValidation -join [Environment]::NewLine)" }
    Move-Item -LiteralPath $stagedArchive -Destination $archivePath
    Write-Output "Created $archivePath for $($manifest.id) $($manifest.version). No plugin code was loaded."
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
