[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(overlay-(quick-access|device-core|device-plugin)-(1280|1920)|settings-(system|quick-access|appearance)-(1024|1280))$')]
    [string[]]$Case
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# Only explicitly named, already rendered cases are promoted. The test runner never writes baselines.
foreach ($name in $Case) {
    $actual = Join-Path $root "TestResults/ui/$name/actual.png"
    if (-not (Test-Path -LiteralPath $actual -PathType Leaf)) {
        throw "No render for $name. Run the UI tests and review their actual.png first."
    }
}
$destination = Join-Path $root 'tests/WSGM.UiTests/Baselines'
[void](New-Item -ItemType Directory -Force -Path $destination)
foreach ($name in $Case) {
    Copy-Item -LiteralPath (Join-Path $root "TestResults/ui/$name/actual.png") -Destination (Join-Path $destination "$name.png")
    Write-Host "Updated $name. Rerun the UI tests before committing."
}
