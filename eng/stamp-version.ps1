<#
.SYNOPSIS
    Stamps the version of a release tag into src\WSGM\WSGM.csproj and src\WSGM\app.manifest.

.DESCRIPTION
    The release workflow runs this in every job that builds from the tag. The csproj <Version> covers
    the executable metadata, the setup (WSGM.Setup reads it) and the wsgmVersion the packers stamp.
    The SxS assembly identity requires four decimal numbers, so it takes only the tag's numeric core,
    padded to four parts: a prerelease tag (v1.4.0-rc1) would otherwise yield "1.4.0-rc1.0", a
    manifest Windows refuses at process creation (SxS error 14001). A tag without a numeric core
    fails. check-version-sync.ps1 checks the same derivation on the committed tree.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$version = $Tag -replace '^v', ''
$core = ($version -split '[-+]')[0]
if ($core -notmatch '^\d+(\.\d+){0,3}$') {
    throw "Tag version '$version' has no numeric core; refusing to stamp an invalid assemblyIdentity."
}
$parts = @($core.Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$manifestVersion = $parts -join '.'

$csproj = Join-Path $root 'src\WSGM\WSGM.csproj'
(Get-Content -LiteralPath $csproj) -replace '<Version>.*</Version>', "<Version>$version</Version>" |
    Set-Content -LiteralPath $csproj
$manifest = Join-Path $root 'src\WSGM\app.manifest'
(Get-Content -LiteralPath $manifest) -replace 'assemblyIdentity version="[^"]*"', "assemblyIdentity version=`"$manifestVersion`"" |
    Set-Content -LiteralPath $manifest

Write-Host "Stamped version $version (manifest identity $manifestVersion)."
