<#[
.SYNOPSIS
    Validates the handwritten 2.0 capability manifest and writes or checks its generated report.
#>
[CmdletBinding()]
param(
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root "docs\2.0-traceability.manifest.json"
$reportPath = Join-Path $root "docs\2.0-traceability.md"
$planPath = Join-Path $root "_plan\implementation-todo.md"
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$errors = [Collections.Generic.List[string]]::new()

function Add-TraceabilityError([string]$Message) {
    $errors.Add($Message)
}

function Assert-RequiredProperty([object]$Value, [string]$Name, [string]$Context) {
    if ($null -eq $Value -or $null -eq $Value.PSObject.Properties[$Name]) {
        Add-TraceabilityError "$Context is missing required property '$Name'."
        return $false
    }

    return $true
}

function Assert-OrderedUniqueStrings([object[]]$Values, [string]$Context) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $previous = $null
    foreach ($rawValue in $Values) {
        $value = [string]$rawValue
        if ([string]::IsNullOrWhiteSpace($value)) {
            Add-TraceabilityError "$Context contains an empty value."
            continue
        }
        if (-not $seen.Add($value)) {
            Add-TraceabilityError "$Context contains duplicate '$value'."
        }
        if ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $value) -ge 0) {
            Add-TraceabilityError "$Context must be strictly ordinal-sorted ('$previous' before '$value')."
        }
        $previous = $value
    }
}

function Assert-RepositoryPath([string]$Path, [string]$Context, [string[]]$Prefixes) {
    if ([string]::IsNullOrWhiteSpace($Path) -or
        [IO.Path]::IsPathRooted($Path) -or
        $Path.Contains("\") -or
        $Path.Split("/").Contains("..")) {
        Add-TraceabilityError "$Context has invalid repository-relative path '$Path'."
        return
    }

    if ($Prefixes.Count -gt 0 -and -not ($Prefixes | Where-Object { $Path.StartsWith($_, [StringComparison]::Ordinal) })) {
        Add-TraceabilityError "$Context path '$Path' is outside its allowed roots: $($Prefixes -join ', ')."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $root $Path))) {
        Add-TraceabilityError "$Context path does not exist: $Path"
    }
}

function Assert-IdentifierReferences(
    [object[]]$Values,
    [string]$Pattern,
    [string]$Context,
    [string]$PlanText) {
    if ($Values.Count -eq 0) {
        Add-TraceabilityError "$Context must contain at least one identifier."
        return
    }

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $previousOrder = -1
    foreach ($rawValue in $Values) {
        $value = [string]$rawValue
        if (-not $seen.Add($value)) {
            Add-TraceabilityError "$Context contains duplicate '$value'."
        }
        if ($value -notmatch $Pattern) {
            Add-TraceabilityError "$Context contains invalid identifier '$value'."
            continue
        }
        $order = if ($value -match '^P(\d+)-(\d{3})$') {
            ([int]$Matches[1] * 1000) + [int]$Matches[2]
        }
        else {
            [int]($value.Substring($value.IndexOf('-') + 1))
        }
        if ($order -le $previousOrder) {
            Add-TraceabilityError "$Context must be naturally sorted ('$value' is out of order)."
        }
        $previousOrder = $order
        $escaped = [regex]::Escape($value)
        if ($PlanText -notmatch "(?<![A-Z0-9-])$escaped(?![A-Z0-9-])") {
            Add-TraceabilityError "$Context references '$value', which is absent from the 2.0 plan."
        }
    }
}

function Add-MarkdownLine([Text.StringBuilder]$Builder, [string]$Line = "") {
    [void]$Builder.Append($Line).Append("`n")
}

function Escape-MarkdownCell([string]$Value) {
    return $Value.Replace("|", "\|").Replace("`r", " ").Replace("`n", " ")
}

function Format-RepositoryLink([string]$Path) {
    $target = if ($Path.StartsWith("docs/", [StringComparison]::Ordinal)) {
        $Path.Substring("docs/".Length)
    }
    else {
        "../$Path"
    }
    return "[$Path]($target)"
}

function Format-PathList([object[]]$Paths) {
    return (@($Paths | ForEach-Object { Format-RepositoryLink ([string]$_) }) -join "<br>")
}

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Traceability source manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 64
$planText = Get-Content -LiteralPath $planPath -Raw
foreach ($property in @("schemaVersion", "release", "scope", "components", "capabilities")) {
    [void](Assert-RequiredProperty $manifest $property "Traceability manifest")
}
if ([int]$manifest.schemaVersion -ne 1) {
    Add-TraceabilityError "Traceability manifest schemaVersion must be 1."
}
if ([string]$manifest.release -cne "2.0") {
    Add-TraceabilityError "Traceability manifest release must be exactly '2.0'."
}
if ([string]::IsNullOrWhiteSpace([string]$manifest.scope)) {
    Add-TraceabilityError "Traceability manifest scope must explain its implemented-surface boundary."
}

$components = @($manifest.components)
$componentPaths = @($components | ForEach-Object { [string]$_.path })
Assert-OrderedUniqueStrings $componentPaths "components"
$declaredComponentPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($componentPath in $componentPaths) {
    [void]$declaredComponentPaths.Add($componentPath)
}
$discoveredComponents = @(
    Get-ChildItem -Path (Join-Path $root "src"), (Join-Path $root "plugins") `
        -Filter "*.csproj" -File -Recurse |
        Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
        ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace("\", "/") }
    Get-ChildItem -Path (Join-Path $root "native") -Filter "Cargo.toml" -File -Depth 1 |
        ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace("\", "/") }
) | Sort-Object -CaseSensitive

foreach ($path in $discoveredComponents) {
    if (-not $declaredComponentPaths.Contains($path)) {
        Add-TraceabilityError "Production component is neither covered nor explicitly excluded: $path"
    }
}
foreach ($path in $componentPaths) {
    Assert-RepositoryPath $path "component" @("src/", "plugins/", "native/")
    if ($path -notin $discoveredComponents) {
        Add-TraceabilityError "Declared component is not a discovered production project/workspace: $path"
    }
}

$coveredComponentDirectories = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::Ordinal)
foreach ($component in $components) {
    $path = [string]$component.path
    $disposition = [string]$component.disposition
    if ($disposition -notin @("covered", "excluded")) {
        Add-TraceabilityError "Component '$path' has invalid disposition '$disposition'."
        continue
    }
    if ($disposition -eq "excluded") {
        if ($null -eq $component.PSObject.Properties["reason"] -or
            [string]::IsNullOrWhiteSpace([string]$component.reason)) {
            Add-TraceabilityError "Excluded component '$path' needs a concrete reason."
        }
        continue
    }

    $directory = [IO.Path]::GetDirectoryName($path).Replace("\", "/") + "/"
    $coveredComponentDirectories.Add($path, $directory)
}

$capabilities = @($manifest.capabilities)
$capabilityIds = @($capabilities | ForEach-Object { [string]$_.id })
Assert-OrderedUniqueStrings $capabilityIds "capabilities"
$operationIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$usedComponents = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$allowedEvidenceKinds = @(
    "contract",
    "documented-device",
    "documented-live",
    "plan-status",
    "source-review"
)

foreach ($capability in $capabilities) {
    $capabilityId = [string]$capability.id
    foreach ($property in @("id", "title", "owner", "operations")) {
        [void](Assert-RequiredProperty $capability $property "Capability '$capabilityId'")
    }
    if ($capabilityId -notmatch "^[a-z0-9]+(?:[.-][a-z0-9]+)*$") {
        Add-TraceabilityError "Capability ID is not stable lower-case dotted/kebab text: $capabilityId"
    }
    Assert-RepositoryPath ([string]$capability.owner) "Capability '$capabilityId' owner" @("src/", "plugins/", "native/", "eng")

    $operations = @($capability.operations)
    if ($operations.Count -eq 0) {
        Add-TraceabilityError "Capability '$capabilityId' has no shipped/implemented operations."
        continue
    }
    $idsWithinCapability = @($operations | ForEach-Object { [string]$_.id })
    Assert-OrderedUniqueStrings $idsWithinCapability "Capability '$capabilityId' operations"

    foreach ($operation in $operations) {
        $operationId = [string]$operation.id
        foreach ($property in @(
            "id", "title", "requirements", "tasks", "production", "tests", "evidence", "documentation")) {
            [void](Assert-RequiredProperty $operation $property "Operation '$operationId'")
        }
        if (-not $operationIds.Add($operationId)) {
            Add-TraceabilityError "Operation ID is duplicated: $operationId"
        }
        if (-not $operationId.StartsWith("$capabilityId.", [StringComparison]::Ordinal)) {
            Add-TraceabilityError "Operation '$operationId' must be namespaced by '$capabilityId'."
        }
        if ([string]::IsNullOrWhiteSpace([string]$operation.title)) {
            Add-TraceabilityError "Operation '$operationId' has no title."
        }

        Assert-IdentifierReferences @($operation.requirements) "^INV-\d{3}$" `
            "Operation '$operationId' requirements" $planText
        Assert-IdentifierReferences @($operation.tasks) "^P\d+-\d{3}$" `
            "Operation '$operationId' tasks" $planText

        foreach ($field in @("production", "tests", "documentation")) {
            $paths = @($operation.$field)
            if ($paths.Count -eq 0) {
                Add-TraceabilityError "Operation '$operationId' field '$field' must not be empty."
                continue
            }
            Assert-OrderedUniqueStrings $paths "Operation '$operationId' $field"
            $prefixes = switch ($field) {
                "production" { @("src/", "plugins/", "native/", "eng/", "installer/") }
                "tests" { @("tests/", "native/", "eng/") }
                default { @("docs/", "_plan/", "src/", "plugins/") }
            }
            foreach ($path in $paths) {
                Assert-RepositoryPath ([string]$path) "Operation '$operationId' $field" $prefixes
            }
        }

        foreach ($productionPath in @($operation.production)) {
            $matches = @($coveredComponentDirectories.GetEnumerator() | Where-Object {
                ([string]$productionPath).StartsWith($_.Value, [StringComparison]::Ordinal)
            })
            if ($matches.Count -eq 0) {
                # Repository governance/build scripts support shipped operations but are not projects.
                if (-not ([string]$productionPath).StartsWith("eng/", [StringComparison]::Ordinal) -and
                    -not ([string]$productionPath).StartsWith("installer/", [StringComparison]::Ordinal)) {
                    Add-TraceabilityError "Operation '$operationId' production path is outside a covered component: $productionPath"
                }
            }
            else {
                foreach ($match in $matches) {
                    [void]$usedComponents.Add([string]$match.Key)
                }
            }
        }

        $evidence = @($operation.evidence)
        if ($evidence.Count -eq 0) {
            Add-TraceabilityError "Operation '$operationId' evidence must not be empty."
        }
        foreach ($item in $evidence) {
            foreach ($property in @("kind", "path", "note")) {
                [void](Assert-RequiredProperty $item $property "Operation '$operationId' evidence")
            }
            if ([string]$item.kind -notin $allowedEvidenceKinds) {
                Add-TraceabilityError "Operation '$operationId' has invalid evidence kind '$($item.kind)'."
            }
            if ([string]::IsNullOrWhiteSpace([string]$item.note)) {
                Add-TraceabilityError "Operation '$operationId' has evidence without a bounded explanation."
            }
            Assert-RepositoryPath ([string]$item.path) "Operation '$operationId' evidence" `
                @("docs/", "_plan/", "src/", "plugins/", "tests/", "native/")
        }
    }
}

foreach ($componentPath in $coveredComponentDirectories.Keys) {
    if (-not $usedComponents.Contains($componentPath)) {
        Add-TraceabilityError "Covered component has no traced production operation: $componentPath"
    }
}

$testProjectDirectories = @(
    Get-ChildItem -Path (Join-Path $root "tests") -Filter "*.csproj" -File -Recurse |
        Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
        ForEach-Object { [IO.Path]::GetDirectoryName([IO.Path]::GetRelativePath($root, $_.FullName)).Replace("\", "/") + "/" }
) | Sort-Object -CaseSensitive
$allTestPaths = @($capabilities | ForEach-Object { $_.operations } | ForEach-Object { $_.tests })
foreach ($testProjectDirectory in $testProjectDirectories) {
    if (-not ($allTestPaths | Where-Object {
        ([string]$_).StartsWith($testProjectDirectory, [StringComparison]::Ordinal)
    })) {
        Add-TraceabilityError "Test project has no traceability reference: $testProjectDirectory"
    }
}

if ($errors.Count -gt 0) {
    throw "Traceability validation failed:`n - $($errors -join "`n - ")"
}

$builder = [Text.StringBuilder]::new()
Add-MarkdownLine $builder "<!-- Generated by eng/update-traceability.ps1 from docs/2.0-traceability.manifest.json. Do not edit. -->"
Add-MarkdownLine $builder
Add-MarkdownLine $builder "# WSGM 2.0 implemented capability traceability"
Add-MarkdownLine $builder
Add-MarkdownLine $builder ([string]$manifest.scope)
Add-MarkdownLine $builder
Add-MarkdownLine $builder "The handwritten source is [docs/2.0-traceability.manifest.json](2.0-traceability.manifest.json). ``eng/update-traceability.ps1 -Check`` validates coverage and fails when this report drifts."
Add-MarkdownLine $builder
Add-MarkdownLine $builder "| Release | Capabilities | Operations | Covered components | Explicitly excluded components |"
Add-MarkdownLine $builder "| --- | ---: | ---: | ---: | ---: |"
$coveredCount = @($components | Where-Object { $_.disposition -eq "covered" }).Count
$excludedCount = @($components | Where-Object { $_.disposition -eq "excluded" }).Count
Add-MarkdownLine $builder "| $($manifest.release) | $($capabilities.Count) | $($operationIds.Count) | $coveredCount | $excludedCount |"
Add-MarkdownLine $builder
Add-MarkdownLine $builder "## Component boundary"
Add-MarkdownLine $builder
Add-MarkdownLine $builder "| Component | Disposition | Reason |"
Add-MarkdownLine $builder "| --- | --- | --- |"
foreach ($component in $components) {
    $reason = if ($null -ne $component.PSObject.Properties["reason"]) {
        Escape-MarkdownCell ([string]$component.reason)
    }
    else {
        "Traced below"
    }
    Add-MarkdownLine $builder "| $(Format-RepositoryLink ([string]$component.path)) | $($component.disposition) | $reason |"
}

foreach ($capability in $capabilities) {
    Add-MarkdownLine $builder
    Add-MarkdownLine $builder "## $($capability.title)"
    Add-MarkdownLine $builder
    Add-MarkdownLine $builder "Owner: $(Format-RepositoryLink ([string]$capability.owner))"
    Add-MarkdownLine $builder
    Add-MarkdownLine $builder "| Operation | Requirements | Tasks | Production | Tests | Evidence | Documentation |"
    Add-MarkdownLine $builder "| --- | --- | --- | --- | --- | --- | --- |"
    foreach ($operation in @($capability.operations)) {
        $evidenceText = @($operation.evidence | ForEach-Object {
            "$(Format-RepositoryLink ([string]$_.path)) ($($_.kind): $(Escape-MarkdownCell ([string]$_.note)))"
        }) -join "<br>"
        $title = Escape-MarkdownCell ([string]$operation.title)
        Add-MarkdownLine $builder "| **$($operation.id)** — $title | $(@($operation.requirements) -join '<br>') | $(@($operation.tasks) -join '<br>') | $(Format-PathList @($operation.production)) | $(Format-PathList @($operation.tests)) | $evidenceText | $(Format-PathList @($operation.documentation)) |"
    }
}

$expected = $builder.ToString()
if ($Check) {
    if (-not (Test-Path -LiteralPath $reportPath)) {
        throw "Generated traceability report is missing. Run eng/update-traceability.ps1."
    }
    $actual = [IO.File]::ReadAllText($reportPath).Replace("`r`n", "`n")
    if ($actual -cne $expected) {
        throw "Generated traceability report drifted. Run eng/update-traceability.ps1 and review the result."
    }
    Write-Host "Traceability manifest and generated report are current ($($operationIds.Count) operations)."
}
else {
    [IO.File]::WriteAllText($reportPath, $expected, $utf8NoBom)
    Write-Host "Wrote $reportPath ($($operationIds.Count) operations)."
}
