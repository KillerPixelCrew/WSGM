[CmdletBinding()]
param(
    [switch]$Fix,
    [switch]$SkipPrettier
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipPrettier) {
    if (-not (Test-Path "node_modules")) {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    }

    if ($Fix) {
        npm run format
    }
    else {
        npm run format:check
    }
    if ($LASTEXITCODE -ne 0) { throw "Prettier check failed" }
}

# The vendored Rust libraries are validated and built before the .NET build,
# which needs their staged output present. -Validate adds each library's own
# gates (clippy as errors, unit tests) so a change there fails here rather than
# in a release build.
& "$PSScriptRoot\build-steam-input-lease.ps1" -Validate
& "$PSScriptRoot\build-radio.ps1" -Validate

dotnet restore WSGM.slnx
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

$formatArgs = @("format", "WSGM.slnx", "whitespace", "--no-restore", "--verbosity", "minimal")
if (-not $Fix) { $formatArgs += "--verify-no-changes" }
& dotnet @formatArgs
if ($LASTEXITCODE -ne 0) { throw "C# whitespace format check failed" }

$styleArgs = @("format", "WSGM.slnx", "style", "--no-restore", "--severity", "warn", "--verbosity", "minimal")
if (-not $Fix) { $styleArgs += "--verify-no-changes" }
& dotnet @styleArgs
if ($LASTEXITCODE -ne 0) { throw "C# style check failed" }

$analyzerArgs = @("format", "WSGM.slnx", "analyzers", "--no-restore", "--severity", "warn", "--verbosity", "minimal")
if (-not $Fix) { $analyzerArgs += "--verify-no-changes" }
& dotnet @analyzerArgs
if ($LASTEXITCODE -ne 0) { throw "C# analyzer check failed" }

dotnet build WSGM.slnx --configuration Release --no-restore --warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

dotnet test WSGM.slnx --configuration Release --no-build --settings coverlet.runsettings `
    --collect:"XPlat Code Coverage" --results-directory TestResults --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }
