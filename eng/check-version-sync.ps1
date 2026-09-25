<#
.SYNOPSIS
    Fails when a hand-maintained copy of the WSGM version disagrees with WSGM.csproj.

.DESCRIPTION
    The csproj <Version> is the release version source. One other file carries a copy that nothing
    stamps on a local build: the SxS assembly identity in src\WSGM\app.manifest. WSGM.Setup reads
    the csproj directly. The release workflow stamps both from the tag; this check keeps local
    builds and the committed tree consistent too.

    The manifest identity takes the numeric core of the version padded to four parts, which is
    exactly what the release workflow derives.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

$csproj = Get-Content -LiteralPath (Join-Path $root "src\WSGM\WSGM.csproj") -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "No <Version> found in WSGM.csproj" }
$version = $Matches[1].Trim()

$core = ($version -split '[-+]')[0]
if ($core -notmatch '^\d+(\.\d+){0,3}$') { throw "WSGM.csproj version '$version' has no numeric core." }
$parts = @($core.Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$manifestExpected = $parts -join '.'

$problems = [System.Collections.Generic.List[string]]::new()

$manifest = Get-Content -LiteralPath (Join-Path $root "src\WSGM\app.manifest") -Raw
if ($manifest -notmatch '<assemblyIdentity\s+version="([^"]+)"') {
    $problems.Add("src\WSGM\app.manifest has no assemblyIdentity version.")
}
elseif ($Matches[1] -ne $manifestExpected) {
    $problems.Add("src\WSGM\app.manifest assemblyIdentity is $($Matches[1]); WSGM.csproj $version needs $manifestExpected.")
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "Version copies disagree with WSGM.csproj."
}

Write-Host "Version $version is consistent across the csproj and the app manifest."
