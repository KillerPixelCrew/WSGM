# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
Builds and archives a common plugin without loading or installing it.
.DESCRIPTION
Uses a new staging directory and create-new archive publication. The manifest is validated by this
checkout's common Plugin SDK through plugin-manifest.cs, the reader the host uses at discovery. This
command then checks the common category, entry file and package contents before creating the
archive. The host still resolves dependencies at discovery.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Archive
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'device-package-output.ps1')
$projectPath = (Resolve-Path -LiteralPath $Project).Path
$archivePath = [IO.Path]::GetFullPath($Archive)
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
    & dotnet publish $projectPath -c Release --no-self-contained -o $payload
    if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed ($LASTEXITCODE)." }
    $manifestPath = Join-Path $payload 'plugin.wsgm.json'
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
