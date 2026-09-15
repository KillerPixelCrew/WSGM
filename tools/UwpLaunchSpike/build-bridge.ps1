[CmdletBinding()]
param(
    [string] $MinHookSource,
    [string] $OutputDirectory = 'publish/uwp-spike-investigation'
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $MinHookSource) {
    $crate = Get-ChildItem (Join-Path $env:USERPROFILE '.cargo/registry/src') -Directory |
        ForEach-Object { Get-ChildItem $_.FullName -Directory -Filter 'minhook-sys-0.1.1' } |
        Select-Object -First 1
    if (-not $crate) {
        throw 'Restore external/steam-input-lease Cargo dependencies, or supply -MinHookSource for minhook-sys 0.1.1.'
    }
    $MinHookSource = Join-Path $crate.FullName 'minhook'
}

$buildDirectory = Join-Path $repository 'publish/uwp-bridge-build'
$destination = [IO.Path]::GetFullPath($OutputDirectory, $repository)
& cmake -S (Join-Path $PSScriptRoot 'Bridge') -B $buildDirectory -G 'Visual Studio 17 2022' -A x64 "-DMINHOOK_SOURCE=$MinHookSource"
if ($LASTEXITCODE -ne 0) { throw "Bridge configure failed ($LASTEXITCODE)." }
& cmake --build $buildDirectory --config Release
if ($LASTEXITCODE -ne 0) { throw "Bridge build failed ($LASTEXITCODE)." }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $buildDirectory 'Release/WsgmUwpBridge.dll') -Destination $destination
Copy-Item -LiteralPath (Join-Path $buildDirectory 'Release/WsgmUwpInputProbe.dll') -Destination $destination
Copy-Item -LiteralPath (Join-Path $MinHookSource 'LICENSE.txt') -Destination (Join-Path $destination 'MinHook-LICENSE.txt')
