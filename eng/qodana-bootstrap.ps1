<#
.SYNOPSIS
Builds WSGM and collects test coverage before Qodana for .NET analyses it.

.DESCRIPTION
qodana.yaml runs this as its bootstrap on a Windows CI runner. The solution needs the Steam Input
Lease and VIIPER libraries built from their submodules, as build.ps1 does. The library gates
(clippy, tests, export checks) stay in ci.yml.

Only WSGM.Tests carries the coverage collector, as in verify.ps1. Qodana reads the Cobertura report
from .qodana\code-coverage, which is the native-mode default. coverlet writes it into a per-run
subdirectory, so it is copied to the top of that directory.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$coverage = Join-Path $root ".qodana\code-coverage"
$testResults = Join-Path $root "TestResults\Qodana"

Push-Location $root
try {
    & "$PSScriptRoot\build-steam-input-lease.ps1"
    & "$PSScriptRoot\build-viiper.ps1"

    dotnet restore WSGM.slnx
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

    dotnet build WSGM.slnx --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    if (Test-Path -LiteralPath $testResults) { Remove-Item -Recurse -Force -LiteralPath $testResults }
    dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --configuration Release --no-build `
        --settings coverlet.runsettings --collect:"XPlat Code Coverage" `
        --results-directory $testResults
    if ($LASTEXITCODE -ne 0) { throw "WSGM coverage test run failed" }

    $report = Get-ChildItem -LiteralPath $testResults -Recurse -Filter "coverage.cobertura.xml" |
        Select-Object -First 1
    if (-not $report) { throw "coverlet did not write coverage.cobertura.xml" }

    New-Item -ItemType Directory -Force -Path $coverage | Out-Null
    Copy-Item -LiteralPath $report.FullName -Destination (Join-Path $coverage "coverage.cobertura.xml") -Force
}
finally {
    Pop-Location
}
