[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $root "WSGM.slnx"

function Normalize-RepositoryPath([string]$Path) {
    return $Path.Replace("\", "/").TrimStart("./")
}

function Read-Project([string]$RelativePath) {
    $fullPath = Join-Path $root ($RelativePath.Replace("/", "\"))
    return [xml](Get-Content -LiteralPath $fullPath -Raw)
}

function Read-Property([xml]$Project, [string]$Name) {
    $node = $Project.SelectSingleNode("/Project/PropertyGroup/$Name")
    if ($null -eq $node) { return $null }
    return $node.InnerText.Trim()
}

function Assert-Property(
    [string]$ProjectPath,
    [string]$Name,
    [string]$Expected
) {
    $project = Read-Project $ProjectPath
    $actual = Read-Property $project $Name
    if ($actual -cne $Expected) {
        throw "$ProjectPath must declare <$Name>$Expected</$Name>; found '$actual'."
    }
}

function Assert-Precedes(
    [System.Collections.Generic.Dictionary[string, int]]$Order,
    [string]$Dependency,
    [string]$Consumer
) {
    if ($Order[$Dependency] -ge $Order[$Consumer]) {
        throw "WSGM.slnx must list dependency '$Dependency' before '$Consumer'."
    }
}

[xml]$solution = Get-Content -LiteralPath $solutionPath -Raw
$solutionProjects = @($solution.SelectNodes("/Solution/Folder/Project") | ForEach-Object {
    Normalize-RepositoryPath $_.Path
})
if ($solutionProjects.Count -ne ($solutionProjects | Sort-Object -Unique).Count) {
    throw "WSGM.slnx contains a duplicate project entry."
}

$repositoryProjects = @(
    Get-ChildItem -LiteralPath (Join-Path $root "src"), (Join-Path $root "plugins"), (Join-Path $root "tests") `
        -Filter "*.csproj" -File -Recurse |
        Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
        ForEach-Object { Normalize-RepositoryPath ([IO.Path]::GetRelativePath($root, $_.FullName)) } |
        Sort-Object
)
$missing = @($repositoryProjects | Where-Object { $_ -notin $solutionProjects })
$unknown = @($solutionProjects | Where-Object { $_ -notin $repositoryProjects })
if ($missing.Count -gt 0 -or $unknown.Count -gt 0) {
    throw "WSGM.slnx project drift. Missing: [$($missing -join ', ')]. Unknown: [$($unknown -join ', ')]."
}

$packageJsonPath = Join-Path $root "package.json"
$packageLockPath = Join-Path $root "package-lock.json"
if (-not (Test-Path -LiteralPath $packageLockPath -PathType Leaf)) {
    throw "The npm release graph requires a checked-in package-lock.json."
}
$packageJson = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json -Depth 8
if ([string]$packageJson.scripts.'steam-assets:verify' -cne "node eng/verify-steam-assets.mjs") {
    throw "package.json must expose the deterministic steam-assets:verify drift gate."
}

$order = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::OrdinalIgnoreCase)
for ($index = 0; $index -lt $solutionProjects.Count; $index++) {
    $order.Add($solutionProjects[$index], $index)
}

$contracts = "src/WSGM.Device.Contracts/WSGM.Device.Contracts.csproj"
$sdk = "src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj"
$labCore = "src/WSGM.DeviceLab.Core/WSGM.DeviceLab.Core.csproj"
Assert-Precedes $order $contracts $sdk
Assert-Precedes $order $contracts $labCore
Assert-Precedes $order $sdk "src/WSGM.DeviceHost/WSGM.DeviceHost.csproj"
Assert-Precedes $order $sdk "plugins/WSGM.Device.Msi.Claw8A2Vm/WSGM.Device.Msi.Claw8A2Vm.csproj"
Assert-Precedes $order $labCore "src/WSGM.DeviceLab.Cli/WSGM.DeviceLab.Cli.csproj"
Assert-Precedes $order $labCore "src/WSGM.DeviceLab.Gui/WSGM.DeviceLab.Gui.csproj"
Assert-Precedes $order $labCore "src/WSGM.Device.ProbeHost/WSGM.Device.ProbeHost.csproj"

$aotProjects = @(
    "src/WSGM/WSGM.csproj",
    "src/WSGM.Launch/WSGM.Launch.csproj",
    "src/WSGM.LogonService/WSGM.LogonService.csproj"
)
foreach ($projectPath in $aotProjects) {
    Assert-Property $projectPath "PublishAot" "true"
    Assert-Property $projectPath "RuntimeIdentifier" "win-x64"
}

$jitExecutables = @(
    "src/WSGM.DeviceHost/WSGM.DeviceHost.csproj",
    "src/WSGM.DeviceLab.Cli/WSGM.DeviceLab.Cli.csproj",
    "src/WSGM.DeviceLab.Gui/WSGM.DeviceLab.Gui.csproj",
    "src/WSGM.Device.ProbeHost/WSGM.Device.ProbeHost.csproj"
)
foreach ($projectPath in $jitExecutables) {
    Assert-Property $projectPath "PublishAot" "false"
    Assert-Property $projectPath "PublishSingleFile" "false"
    Assert-Property $projectPath "SelfContained" "true"
    Assert-Property $projectPath "RuntimeIdentifier" "win-x64"
}

$wsgm = Read-Project "src/WSGM/WSGM.csproj"
$wsgmReferences = @($wsgm.SelectNodes("/Project/ItemGroup/ProjectReference") | ForEach-Object {
    Normalize-RepositoryPath $_.Include
})
$forbiddenReference = $wsgmReferences | Where-Object {
    $_ -match "WSGM\.Device\.(Sdk|Host|ProbeHost|DeviceLab|Msi)"
}
if ($null -ne $forbiddenReference) {
    throw "NativeAOT WSGM references a JIT-only device project: $forbiddenReference"
}

Write-Host "Solution build graph and AOT/JIT boundaries are explicit and complete."
