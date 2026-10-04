[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-common.ps1')
$runtimeLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'webview2-runtime.lock.json') -Raw | ConvertFrom-Json
$runtimeCache = Join-Path $PSScriptRoot '..\artifacts\webview2'
New-Item -ItemType Directory -Path $runtimeCache, $Destination -Force | Out-Null
$runtimeFile = Join-Path $runtimeCache $runtimeLock.asset
Get-PinnedAsset -Url $runtimeLock.url -Path $runtimeFile -Sha256 $runtimeLock.sha256 `
    -SignerSubjectPattern 'CN=Microsoft Corporation(?:,|$)'
Copy-Item -LiteralPath $runtimeFile -Destination $Destination -Force
Write-Host "Verified WebView2 offline runtime $($runtimeLock.version)."
