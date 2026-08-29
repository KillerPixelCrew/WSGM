[CmdletBinding()]
param(
    [switch]$Fix,
    [switch]$SkipPrettier
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# Push/Pop rather than Set-Location: the gate must be runnable from anywhere
# without relocating the caller's shell, including when a step below throws.
Push-Location $root
try {
    if (-not $SkipPrettier) {
        # A directory-existence check alone would keep using a stale Prettier
        # after a package.json/lockfile bump, so the local gate would format
        # differently from CI's always-fresh `npm ci` — a locally green run that
        # fails format:check in CI with no visible cause.
        if (-not (Test-Path "node_modules") -or
            (Get-Item "package-lock.json").LastWriteTimeUtc -gt (Get-Item "node_modules").LastWriteTimeUtc) {
            npm ci --ignore-scripts --prefer-offline --no-audit --no-fund `
                --fetch-retries=2 --fetch-timeout=30000
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

    # Repository-owned Steam UI assets use only Node built-ins for their drift
    # check, so this remains available in offline release builds and when the
    # caller deliberately skips Prettier.
    npm run steam-assets:verify
    if ($LASTEXITCODE -ne 0) { throw "Steam UI asset drift check failed" }

    & "$PSScriptRoot\check-agent-guidance.ps1"

    # Cheap source scan, before anything is built: a test or probe that can resolve the real
    # %LOCALAPPDATA%\WSGM directory is a defect regardless of whether it compiles.
    & "$PSScriptRoot\check-no-live-data-paths.ps1"

    # The vendored Rust libraries are validated and built before the .NET build,
    # which needs their staged output present. -Validate adds each library's own
    # gates (clippy as errors, unit tests) so a change there fails here rather than
    # in a release build.
    & "$PSScriptRoot\build-steam-input-lease.ps1" -Validate
    & "$PSScriptRoot\build-radio.ps1" -Validate

    dotnet restore WSGM.slnx
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

    # Vendored upstream source is reachable through a project reference but is not
    # ours to restyle: reformatting it would destroy the diff against upstream, which
    # is what makes it re-syncable. Its own gates are the upstream project's.
    $vendored = "third_party/"

    $formatArgs = @("format", "WSGM.slnx", "whitespace", "--no-restore", "--verbosity", "minimal",
        "--exclude", $vendored)
    if (-not $Fix) { $formatArgs += "--verify-no-changes" }
    & dotnet @formatArgs
    if ($LASTEXITCODE -ne 0) { throw "C# whitespace format check failed" }

    $styleArgs = @("format", "WSGM.slnx", "style", "--no-restore", "--severity", "warn", "--verbosity", "minimal",
        "--exclude", $vendored)
    if (-not $Fix) { $styleArgs += "--verify-no-changes" }
    & dotnet @styleArgs
    if ($LASTEXITCODE -ne 0) { throw "C# style check failed" }

    $analyzerArgs = @("format", "WSGM.slnx", "analyzers", "--no-restore", "--severity", "warn", "--verbosity", "minimal",
        "--exclude", $vendored)
    if (-not $Fix) { $analyzerArgs += "--verify-no-changes" }
    & dotnet @analyzerArgs
    if ($LASTEXITCODE -ne 0) { throw "C# analyzer check failed" }

    dotnet build WSGM.slnx --configuration Release --no-restore --warnaserror
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    dotnet test WSGM.slnx --configuration Release --no-build --settings coverlet.runsettings `
        --collect:"XPlat Code Coverage" --results-directory TestResults --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }
}
finally {
    Pop-Location
}
