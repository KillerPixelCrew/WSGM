<#
.SYNOPSIS
Builds WSGM and provides test coverage before Qodana for .NET analyses it.

.DESCRIPTION
qodana.yaml runs this as its bootstrap. The solution needs the Steam Input Lease and VIIPER
libraries built from their submodules, as build.ps1 does. The library gates (clippy, tests, export
checks) stay in the verify and viiper CI jobs.

Qodana reads the Cobertura report from .qodana\code-coverage, which is the native-mode default.
Only WSGM.Tests carries the coverage collector, as in verify.ps1. coverlet writes the report into a
per-run subdirectory, so it is copied to the top of that directory.

The qodana.yaml bootstrap cannot pass arguments per run, so both parameters also read environment
variables. Without either, the script builds everything and runs the tests itself.

.PARAMETER UseStagedNative
Use the libraries already staged in src\WSGM\Native instead of building them. CI downloads them
from the verify and viiper jobs. Defaults to true when WSGM_QODANA_USE_STAGED_NATIVE is set.

.PARAMETER CoverageFrom
Directory that already holds a WSGM.Tests coverage.cobertura.xml, searched recursively. The tests
are not run when it is given. Defaults to WSGM_QODANA_COVERAGE_FROM.
#>
[CmdletBinding()]
param(
    [switch]$UseStagedNative = [bool]$env:WSGM_QODANA_USE_STAGED_NATIVE,
    [string]$CoverageFrom = $env:WSGM_QODANA_COVERAGE_FROM
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$coverage = Join-Path $root ".qodana\code-coverage"

Push-Location $root
try {
    if ($UseStagedNative) {
        foreach ($library in @("SteamInputLease\steam_input_lease_ffi.dll", "Viiper\libviiper.dll")) {
            if (-not (Test-Path -LiteralPath (Join-Path $root "src\WSGM\Native\$library"))) {
                throw "Staged native library not found: src\WSGM\Native\$library"
            }
        }
    }
    else {
        & "$PSScriptRoot\build-steam-input-lease.ps1"
        & "$PSScriptRoot\build-viiper.ps1"
    }

    dotnet restore WSGM.slnx
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

    dotnet build WSGM.slnx --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    if (-not $CoverageFrom) {
        $CoverageFrom = Join-Path $root "TestResults\Qodana"
        if (Test-Path -LiteralPath $CoverageFrom) { Remove-Item -Recurse -Force -LiteralPath $CoverageFrom }
        dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --configuration Release --no-build `
            --settings coverlet.runsettings --collect:"XPlat Code Coverage" `
            --results-directory $CoverageFrom
        if ($LASTEXITCODE -ne 0) { throw "WSGM coverage test run failed" }
    }

    $report = Get-ChildItem -LiteralPath $CoverageFrom -Recurse -Filter "coverage.cobertura.xml" |
        Select-Object -First 1
    if (-not $report) { throw "No coverage.cobertura.xml found under $CoverageFrom" }

    New-Item -ItemType Directory -Force -Path $coverage | Out-Null
    Copy-Item -LiteralPath $report.FullName -Destination (Join-Path $coverage "coverage.cobertura.xml") -Force
}
finally {
    Pop-Location
}
