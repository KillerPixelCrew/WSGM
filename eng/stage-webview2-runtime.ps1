[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runtimeLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'webview2-runtime.lock.json') -Raw | ConvertFrom-Json
$runtimeCache = Join-Path $PSScriptRoot '..\artifacts\webview2'
New-Item -ItemType Directory -Path $runtimeCache, $Destination -Force | Out-Null
$runtimeFile = Join-Path $runtimeCache $runtimeLock.asset
if (-not (Test-Path -LiteralPath $runtimeFile) -or (Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash -ne $runtimeLock.sha256) {
    Invoke-WebRequest -Uri $runtimeLock.url -OutFile $runtimeFile
}
if ((Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash -ne $runtimeLock.sha256) {
    throw 'The WebView2 offline runtime does not match its pinned SHA-256.'
}
$runtimeSignature = Get-AuthenticodeSignature -LiteralPath $runtimeFile
if ($runtimeSignature.Status -ne 'Valid' -or $runtimeSignature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation(?:,|$)') {
    throw 'The WebView2 offline runtime does not have a valid Microsoft signature.'
}
Copy-Item -LiteralPath $runtimeFile -Destination $Destination -Force
Write-Host "Verified WebView2 offline runtime $($runtimeLock.version)."
