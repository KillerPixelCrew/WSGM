[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-common.ps1')
$runtimeLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'rtss-runtime.lock.json') -Raw | ConvertFrom-Json
if ($runtimeLock.asset -cne 'rtss.zip' -or $runtimeLock.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
    [IO.Path]::GetFileName([string]$runtimeLock.setupEntry) -cne $runtimeLock.setupEntry) {
    throw 'RTSS runtime pin must name rtss.zip, a SHA-256 digest and a root installer entry.'
}
$runtimeCache = Join-Path $PSScriptRoot '..\artifacts\rtss'
New-Item -ItemType Directory -Path $runtimeCache, $Destination -Force | Out-Null
$runtimeFile = Join-Path $runtimeCache $runtimeLock.asset
# The upstream ZIP is unsigned. Its reviewed SHA-256 pins the complete archive.
Get-PinnedAsset -Url $runtimeLock.url -Path $runtimeFile -Sha256 $runtimeLock.sha256
$archive = [IO.Compression.ZipFile]::OpenRead($runtimeFile)
try {
    $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $runtimeLock.setupEntry })
    if ($entries.Count -ne 1 -or $entries[0].Length -eq 0) {
        throw "RTSS archive must contain exactly one nonempty $($runtimeLock.setupEntry)."
    }
}
finally {
    $archive.Dispose()
}
Copy-Item -LiteralPath $runtimeFile -Destination $Destination -Force
Write-Host "Verified RTSS offline runtime $($runtimeLock.version)."
