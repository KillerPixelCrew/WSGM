<#[
.SYNOPSIS
    Verifies production ownership guidance and the load-bearing CLAUDE.md symlink convention.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$failures = [Collections.Generic.List[string]]::new()

function Relative-Path([string]$Path) {
    return [IO.Path]::GetRelativePath($root, $Path).Replace("\", "/")
}

$agentFiles = Get-ChildItem -LiteralPath $root -Filter "AGENTS.md" -File -Recurse |
    Where-Object { $_.FullName -notmatch "[\\/](\.claude|bin|obj|node_modules|publish|TestResults)[\\/]" }

foreach ($agents in $agentFiles) {
    $directory = $agents.DirectoryName
    $claudePath = Join-Path $directory "CLAUDE.md"
    $relative = Relative-Path $claudePath
    if (-not (Test-Path -LiteralPath $claudePath)) {
        $failures.Add("Missing $relative beside $(Relative-Path $agents.FullName).")
        continue
    }

    $claude = Get-Item -LiteralPath $claudePath -Force
    if ($claude.LinkType -ne "SymbolicLink" -or
        @($claude.Target).Count -ne 1 -or
        [string]$claude.Target -cne "AGENTS.md") {
        $failures.Add("$relative must be a symbolic link whose exact target is AGENTS.md.")
    }

    $index = & git -C $root ls-files -s -- $relative
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($index)) {
        $failures.Add("$relative is not tracked.")
    }
    elseif (-not $index.StartsWith("120000 ", [StringComparison]::Ordinal)) {
        $failures.Add("$relative is tracked with a mode other than 120000.")
    }
}

$productionProjects = @(
    Get-ChildItem -Path (Join-Path $root "src"), (Join-Path $root "plugins") `
        -Filter "*.csproj" -File -Recurse |
        Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" }
)
foreach ($project in $productionProjects) {
    $cursor = $project.Directory
    $guidance = $null
    while ($null -ne $cursor -and $cursor.FullName.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        $candidate = Join-Path $cursor.FullName "AGENTS.md"
        if (Test-Path -LiteralPath $candidate) {
            $guidance = $candidate
            break
        }
        $cursor = $cursor.Parent
    }
    if ($null -eq $guidance -or [IO.Path]::GetFullPath($guidance) -eq (Join-Path $root "AGENTS.md")) {
        $failures.Add("Production project $(Relative-Path $project.FullName) has no scoped AGENTS.md owner.")
    }
}

if ($failures.Count -gt 0) {
    throw "Agent-guidance validation failed:`n - $($failures -join "`n - ")"
}

Write-Host "Agent guidance is scoped and every CLAUDE.md is a tracked AGENTS.md symlink."
