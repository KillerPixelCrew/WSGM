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
    # Asset generation uses TypeScript even when formatting is skipped. Provision once for both
    # paths; -SkipPrettier skips only the formatting command it names.
    if (-not (Test-Path "node_modules") -or
        (Get-Item "package-lock.json").LastWriteTimeUtc -gt (Get-Item "node_modules").LastWriteTimeUtc) {
        npm ci --ignore-scripts --prefer-offline --no-audit --no-fund `
            --fetch-retries=2 --fetch-timeout=30000
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    }

    if (-not $SkipPrettier) {
        if ($Fix) {
            npm run format
        }
        else {
            npm run format:check
        }
        if ($LASTEXITCODE -ne 0) { throw "Prettier check failed" }
    }

    # The shipped asset is generated from its TypeScript source. This rebuilds it
    # into memory and compares, so neither a source edit that was never compiled
    # nor a hand edit of the generated file can ship. It needs node_modules, so it
    # is separate from the built-ins-only check above.
    npm run steam-assets:check
    if ($LASTEXITCODE -ne 0) { throw "Steam UI asset is not current with its TypeScript source" }

    # The toolkit's own check, run against WSGM's composed asset: the ownership claims, exercised
    # on the bytes this build injects. It covers the scenarios that cost device sessions —
    # reclaiming a previous bridge's work rather than tearing it down, and restoring exactly what
    # was displaced. Nothing else in this gate can observe that: the C# tests never execute the
    # injected JavaScript, and the drift check proves only that the asset is current, not correct.
    npm run steam-assets:claims
    if ($LASTEXITCODE -ne 0) { throw "Steam UI ownership claim check failed" }

    & "$PSScriptRoot\check-agent-guidance.ps1"

    # Parse every retained PowerShell entry point in this repository and its recursive submodules.
    # This is syntax-only: deployment, shell, installer, and hardware scripts must never be invoked
    # by the unattended verification gate merely to prove that they still parse.
    $powerShellFiles = @(
        git ls-files --recurse-submodules -- "*.ps1" "*.psm1" |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    )
    if ($LASTEXITCODE -ne 0 -or $powerShellFiles.Count -eq 0) {
        throw "Enumerating tracked PowerShell scripts failed"
    }
    foreach ($powerShellFile in $powerShellFiles) {
        $tokens = $null
        $parseErrors = $null
        [void][Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $root $powerShellFile),
            [ref]$tokens,
            [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            $details = $parseErrors | ForEach-Object {
                "$powerShellFile`:$($_.Extent.StartLineNumber): $($_.Message)"
            }
            throw "PowerShell syntax validation failed:`n$($details -join [Environment]::NewLine)"
        }
    }
    Write-Host "Parsed $($powerShellFiles.Count) tracked PowerShell scripts."

    # Cheap source scan, before anything is built: a test or probe that can resolve the real
    # %LOCALAPPDATA%\WSGM directory is a defect regardless of whether it compiles.
    & "$PSScriptRoot\check-no-live-data-paths.ps1"

    # The manifest identity and the installer's fallback version are hand-maintained copies of the
    # csproj version that no local build stamps.
    & "$PSScriptRoot\check-version-sync.ps1"

    # The committed Ally X Lab download must agree with its hash file and manifest. A source change
    # since the last rebuild is only a warning; the maintainer decides when testers get a new exe.
    & "$PSScriptRoot\allyxlab-download.ps1"

    # The setup step that installs the USB/IP driver carries its own copy of the pinned identity,
    # because it runs where the lock file does not exist. This is what stops the two drifting into
    # a setup that installs a version nobody reviewed.
    & "$PSScriptRoot\assert-controller-pin.ps1"

    # The vendored Rust library is validated and built before the .NET build,
    # which needs its staged output present. -Validate adds the library's own
    # gates (clippy as errors, unit tests) so a change there fails here rather than
    # in a release build.
    & "$PSScriptRoot\build-steam-input-lease.ps1" -Validate

    # This small solution does not benefit from one MSBuild node per logical CPU; on the
    # high-core reference handheld that left dozens of idle child processes after test runs.
    dotnet restore WSGM.slnx -m:1
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

    # Rider's formatter is the layout authority. The same ReSharper engine runs here as
    # jb cleanupcode with the built-in Full Cleanup profile and the solution settings layer, and
    # the gate is that it changes nothing. Nothing under external/ is ours to restyle from here:
    # each submodule has its own gate, and vendored upstream source keeps its diff against upstream
    # (src/WSGM/ThirdParty is a symlink into it).
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }
    dotnet jb cleanupcode WSGM.slnx --settings=WSGM.slnx.DotSettings --profile="Built-in: Full Cleanup" `
        --include="src\**\*.cs;tests\**\*.cs" --exclude="**\obj\**;**\bin\**;src\WSGM\ThirdParty\**" --no-build --verbosity=WARN
    if ($LASTEXITCODE -ne 0) { throw "jb cleanupcode failed" }
    if (-not $Fix) {
        git diff --exit-code --stat -- src tests
        if ($LASTEXITCODE -ne 0) { throw "C# layout differs from Rider's Full Cleanup; run eng/verify.ps1 -Fix" }
    }

    # The documentation diagnostics are build errors already (warnaserror below). Left in the
    # style pass, the fixer for them splices `/// <inheritdoc/>` into the middle of a declaration
    # under -Fix, which corrupted CardButton.cs on 2026-09-03; they are for a person to write.
    $documentationDiagnostics = @("--exclude-diagnostics", "CS1591", "CS1573")
    $formatModes = @(
        @{ Name = "style"; Severity = @("--severity", "warn"); Excluded = $documentationDiagnostics; Failure = "C# style check failed" },
        @{ Name = "analyzers"; Severity = @("--severity", "warn"); Excluded = $documentationDiagnostics; Failure = "C# analyzer check failed" }
    )
    foreach ($mode in $formatModes) {
        $formatArgs = @("format", "WSGM.slnx", $mode.Name, "--no-restore") + $mode.Severity +
            @("--verbosity", "minimal") + $mode.Excluded + @("--exclude", "external/", "--exclude", "src/WSGM/ThirdParty/")
        if (-not $Fix) { $formatArgs += "--verify-no-changes" }
        & dotnet @formatArgs
        if ($LASTEXITCODE -ne 0) { throw $mode.Failure }
    }

    dotnet build WSGM.slnx --configuration Release --no-restore --warnaserror -m:1
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

    dotnet test WSGM.slnx --configuration Release --no-build `
        --logger "console;verbosity=normal" -m:1
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }

    # Only WSGM.Tests carries the coverage collector. The other project suites run above without a
    # collector request, avoiding false "collector not found" diagnostics while still keeping the
    # application's existing coverage artifact.
    dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --configuration Release --no-build `
        --settings coverlet.runsettings --collect:"XPlat Code Coverage" `
        --results-directory TestResults --logger "console;verbosity=normal" -m:1
    if ($LASTEXITCODE -ne 0) { throw "WSGM coverage test run failed" }
}
finally {
    Pop-Location
}
