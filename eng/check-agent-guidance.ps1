<#[
.SYNOPSIS
    Verifies the load-bearing guidance and shared Agent Skill conventions.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$failures = [Collections.Generic.List[string]]::new()

function Relative-Path([string]$Path) {
    $rootFull = [IO.Path]::GetFullPath($root).TrimEnd("\", "/")
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull + "\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Agent-guidance path is outside the repository: $pathFull"
    }

    return $pathFull.Substring($rootFull.Length + 1).Replace("\", "/")
}

# A submodule's guidance belongs to its own repository: its CLAUDE.md is tracked there, so this
# repository's index never sees it and every check below would report it missing.
$submodules = @(& git -C $root ls-files -s |
    Where-Object { $_.StartsWith("160000 ", [StringComparison]::Ordinal) } |
    ForEach-Object { ($_ -split "`t", 2)[1] })
if ($LASTEXITCODE -ne 0) {
    throw "Agent-guidance validation could not list submodules."
}

$agentFiles = Get-ChildItem -LiteralPath $root -Filter "AGENTS.md" -File -Recurse |
    Where-Object { $_.FullName -notmatch "[\\/](\.claude|bin|obj|node_modules|publish|TestResults)[\\/]" }

foreach ($agents in $agentFiles) {
    $agentsRelative = Relative-Path $agents.FullName
    if ($submodules | Where-Object { $agentsRelative.StartsWith("$_/", [StringComparison]::OrdinalIgnoreCase) }) {
        continue
    }

    $directory = $agents.DirectoryName
    $claudePath = Join-Path $directory "CLAUDE.md"
    $relative = Relative-Path $claudePath
    if (-not (Test-Path -LiteralPath $claudePath)) {
        $failures.Add("Missing $relative beside $agentsRelative.")
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

$canonicalSkillsRoot = Join-Path $root ".agents\skills"
$claudeSkillsRoot = Join-Path $root ".claude\skills"
$skillDirectories = @()

if (-not (Test-Path -LiteralPath $canonicalSkillsRoot -PathType Container)) {
    $failures.Add("Missing canonical Agent Skills directory .agents/skills.")
}
else {
    $skillDirectories = @(Get-ChildItem -LiteralPath $canonicalSkillsRoot -Directory -Force |
        Sort-Object -Property Name)
    if ($skillDirectories.Count -eq 0) {
        $failures.Add("Canonical Agent Skills directory .agents/skills is empty.")
    }

    foreach ($skillDirectory in $skillDirectories) {
        $directoryName = $skillDirectory.Name
        $skillPath = Join-Path $skillDirectory.FullName "SKILL.md"
        $skillRelative = Relative-Path $skillPath

        if (-not (Test-Path -LiteralPath $skillPath -PathType Leaf)) {
            $failures.Add("Missing $skillRelative.")
            continue
        }

        $skillIndex = & git -C $root ls-files -s -- $skillRelative
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($skillIndex)) {
            $failures.Add("$skillRelative is not tracked.")
        }

        $source = Get-Content -LiteralPath $skillPath -Raw
        $frontMatter = [regex]::Match(
            $source,
            '\A---\r?\n(?<body>.*?)\r?\n---(?:\r?\n|\z)',
            [Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not $frontMatter.Success) {
            $failures.Add("$skillRelative must start with YAML frontmatter.")
            continue
        }

        $frontMatterBody = $frontMatter.Groups["body"].Value
        $nameMatch = [regex]::Match(
            $frontMatterBody,
            '(?m)^name:[ \t]*(?<value>[^\r\n]+?)[ \t]*\r?$')
        if (-not $nameMatch.Success) {
            $failures.Add("$skillRelative frontmatter is missing name.")
        }
        else {
            $skillName = $nameMatch.Groups["value"].Value
            if ($skillName -cne $directoryName) {
                $failures.Add("$skillRelative name must exactly match directory $directoryName.")
            }
            if ($skillName.Length -gt 64 -or
                $skillName -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
                $failures.Add("$skillRelative name must be at most 64 lowercase letters, digits, or hyphen-separated words.")
            }
        }

        $descriptionMatch = [regex]::Match(
            $frontMatterBody,
            '(?m)^description:[ \t]*(?<value>[^\r\n]*\S)[ \t]*\r?$')
        $descriptionBlockMatch = [regex]::Match(
            $frontMatterBody,
            '(?m)^description:[ \t]*\r?\n[ \t]+(?<value>\S[^\r\n]*)')
        if (-not $descriptionMatch.Success -and -not $descriptionBlockMatch.Success) {
            $failures.Add("$skillRelative frontmatter is missing a non-empty description.")
        }
    }
}

if (-not (Test-Path -LiteralPath $claudeSkillsRoot -PathType Container)) {
    $failures.Add("Missing Claude Agent Skills directory .claude/skills.")
}
else {
    $claudeSkillsDirectory = Get-Item -LiteralPath $claudeSkillsRoot -Force
    if ($null -ne $claudeSkillsDirectory.LinkType) {
        $failures.Add(".claude/skills must be a real directory containing per-skill symlinks.")
    }

    foreach ($skillDirectory in $skillDirectories) {
        $skillName = $skillDirectory.Name
        $aliasPath = Join-Path $claudeSkillsRoot $skillName
        $aliasRelative = Relative-Path $aliasPath
        $expectedTarget = "../../.agents/skills/$skillName"

        if (-not (Test-Path -LiteralPath $aliasPath)) {
            $failures.Add("Missing $aliasRelative for canonical skill $skillName.")
            continue
        }

        $alias = Get-Item -LiteralPath $aliasPath -Force
        $targets = @($alias.Target)
        $actualTarget = if ($targets.Count -eq 1) {
            ([string]$targets[0]).Replace("\", "/")
        }
        else {
            ""
        }
        if ($alias.LinkType -ne "SymbolicLink" -or
            $targets.Count -ne 1 -or
            $actualTarget -cne $expectedTarget) {
            $failures.Add("$aliasRelative must be a symbolic link whose exact target is $expectedTarget.")
        }

        $aliasIndex = & git -C $root ls-files -s -- $aliasRelative
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($aliasIndex)) {
            $failures.Add("$aliasRelative is not tracked.")
        }
        elseif (-not $aliasIndex.StartsWith("120000 ", [StringComparison]::Ordinal)) {
            $failures.Add("$aliasRelative is tracked with a mode other than 120000.")
        }
        else {
            $indexTarget = [string]::Join("`n", @(& git -C $root show ":$aliasRelative"))
            if ($LASTEXITCODE -ne 0 -or $indexTarget -cne $expectedTarget) {
                $failures.Add("$aliasRelative index target must be exactly $expectedTarget.")
            }
        }
    }

    $canonicalNames = @($skillDirectories | ForEach-Object { $_.Name })
    $trackedAliases = @(& git -C $root ls-files -- ".claude/skills/*")
    if ($LASTEXITCODE -ne 0) {
        throw "Agent-guidance validation could not list Claude skill aliases."
    }
    foreach ($trackedAlias in $trackedAliases) {
        $trackedAliasName = [IO.Path]::GetFileName($trackedAlias)
        if ($canonicalNames -cnotcontains $trackedAliasName) {
            $failures.Add("$trackedAlias has no matching canonical Agent Skill.")
        }
    }
}

if ($failures.Count -gt 0) {
    throw "Agent-guidance validation failed:`n - $($failures -join "`n - ")"
}

Write-Host "Agent guidance and shared skill aliases are valid."
