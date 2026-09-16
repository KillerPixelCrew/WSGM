<#
.SYNOPSIS
    Checks, or with -Build rebuilds, the committed Ally X Lab download and its provenance manifest.

.DESCRIPTION
    tools\AllyXLab\Downloads holds the tester's AllyXLab.exe with SHA256.txt and BUILD.json.
    Without -Build this checks that the three agree: the exe's SHA-256 and size match both
    records, the manifest's tool version matches the project, and its snapshot matches its own
    file list. It then reports source files whose content no longer matches the manifest. That
    means the committed exe predates the source; it is a warning, because lab fixes land before the
    maintainer asks for a rebuild, unless -FailOnSourceDrift is given.

    Source hashes are SHA-256 over the file bytes with CRLF normalized to LF, so an autocrlf
    Windows checkout and an LF checkout agree. The snapshot is the SHA-256 of the UTF-8 text made of
    one "<hash>  <path>" line per file, sorted by path, each ending in LF. It is stamped into the
    exe as the SourceSnapshot assembly metadata that the session record reports.

    -Build refuses uncommitted lab sources, publishes the project and rewrites all three files.
    Every rebuild adds about 55 MB to the repository history, so rebuild only when a tester needs
    the new binary.
#>
[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$FailOnSourceDrift
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$lab = Join-Path $root "tools\AllyXLab"
$downloads = Join-Path $lab "Downloads"
$exePath = Join-Path $downloads "AllyXLab.exe"
$manifestPath = Join-Path $downloads "BUILD.json"
$shaPath = Join-Path $downloads "SHA256.txt"
$projectPath = Join-Path $lab "WSGM.AllyXLab.csproj"
$sourceRoots = @(".editorconfig", "Directory.Build.props", "tools/AllyXLab")

function Get-SourceFiles {
    [string[]]$files = @(git -C $root ls-files -- @sourceRoots | Where-Object { $_ -notlike "tools/AllyXLab/Downloads/*" })
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed" }
    [Array]::Sort($files, [StringComparer]::Ordinal)
    return $files
}

function Get-NormalizedHash([string]$relative) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root $relative))
    $normalized = [IO.MemoryStream]::new($bytes.Length)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 13 -and $i + 1 -lt $bytes.Length -and $bytes[$i + 1] -eq 10) { continue }
        $normalized.WriteByte($bytes[$i])
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($normalized.ToArray())).ToLowerInvariant()
}

function Get-Snapshot([System.Collections.IDictionary]$hashes) {
    $paths = [string[]]@($hashes.Keys)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $text = [Text.StringBuilder]::new()
    foreach ($path in $paths) { [void]$text.Append("$($hashes[$path])  $path`n") }
    $bytes = [Text.Encoding]::UTF8.GetBytes($text.ToString())
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-ProjectVersion {
    $project = Get-Content -LiteralPath $projectPath -Raw
    if ($project -notmatch '<Version>([^<]+)</Version>') { throw "No <Version> in $projectPath" }
    return $Matches[1].Trim()
}

function Get-ExeHash {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($exePath))).ToLowerInvariant()
}

if ($Build) {
    $files = Get-SourceFiles
    $dirty = @(git -C $root status --porcelain -- @sourceRoots | Where-Object { $_ -notmatch 'tools/AllyXLab/Downloads/' })
    if ($LASTEXITCODE -ne 0) { throw "git status failed" }
    if ($dirty.Count -gt 0) { throw "Commit the lab sources first; the manifest must describe committed files:`n$($dirty -join "`n")" }

    $hashes = [ordered]@{}
    foreach ($file in $files) { $hashes[$file] = Get-NormalizedHash $file }
    $snapshot = Get-Snapshot $hashes

    $output = Join-Path $root "publish\allyxlab-download"
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    dotnet publish $projectPath -c Release -o $output -m:1 --nologo --warnaserror "-p:SourceSnapshot=$snapshot"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    $published = Join-Path $output "AllyXLab.exe"
    if (-not (Test-Path -LiteralPath $published)) { throw "Publish produced no AllyXLab.exe" }
    Copy-Item -LiteralPath $published -Destination $exePath -Force
    Remove-Item -LiteralPath $output -Recurse -Force

    $sdk = (dotnet --version).Trim()
    $hash = Get-ExeHash
    $manifest = [ordered]@{
        sourceSnapshot = $snapshot
        sourceHashes   = "SHA-256 of each file with CRLF normalized to LF; the snapshot hashes the sorted '<hash>  <path>' lines"
        sourceFiles    = $hashes
        sdk            = $sdk
        runtime        = "win-x64 / .NET 10, the SDK's bundled runtime"
        configuration  = "Release, self-contained, compressed single file"
        sizeBytes      = (Get-Item -LiteralPath $exePath).Length
        sha256         = $hash
        validation     = "Warning-clean publish by eng/allyxlab-download.ps1 -Build. Records no Windows UI or Ally X hardware run."
        toolVersion    = Get-ProjectVersion
    }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4) + "`r`n")
    [IO.File]::WriteAllText($shaPath, "$hash  AllyXLab.exe`r`n")
    Write-Host "Rebuilt AllyXLab.exe ($hash, snapshot $snapshot)."
}

$problems = [System.Collections.Generic.List[string]]::new()
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$exeHash = Get-ExeHash
$exeSize = (Get-Item -LiteralPath $exePath).Length
if ($manifest.sha256 -ne $exeHash) { $problems.Add("BUILD.json sha256 $($manifest.sha256) does not match AllyXLab.exe $exeHash.") }
if ($manifest.sizeBytes -ne $exeSize) { $problems.Add("BUILD.json sizeBytes $($manifest.sizeBytes) does not match AllyXLab.exe ($exeSize).") }
$recorded = ((Get-Content -LiteralPath $shaPath -Raw).Trim() -split '\s+')[0]
if ($recorded -ne $exeHash) { $problems.Add("SHA256.txt $recorded does not match AllyXLab.exe $exeHash.") }
$version = Get-ProjectVersion
if ($manifest.toolVersion -ne $version) { $problems.Add("BUILD.json toolVersion $($manifest.toolVersion) is not the project version $version.") }
if (-not $manifest.ContainsKey("sourceHashes")) {
    $problems.Add("BUILD.json does not declare its source hash rule; rebuild with -Build.")
}
elseif ((Get-Snapshot $manifest.sourceFiles) -ne $manifest.sourceSnapshot) {
    $problems.Add("BUILD.json sourceSnapshot does not match its own sourceFiles.")
}
if ($problems.Count -gt 0) { throw "Ally X Lab download check failed:`n$($problems -join "`n")" }

$current = @{}
foreach ($file in Get-SourceFiles) { $current[$file] = Get-NormalizedHash $file }
$drift = [System.Collections.Generic.List[string]]::new()
foreach ($file in $current.Keys) {
    if (-not $manifest.sourceFiles.ContainsKey($file)) { $drift.Add("added: $file") }
    elseif ($manifest.sourceFiles[$file] -ne $current[$file]) { $drift.Add("changed: $file") }
}
foreach ($file in $manifest.sourceFiles.Keys) {
    if (-not $current.ContainsKey($file)) { $drift.Add("removed: $file") }
}
if ($drift.Count -gt 0) {
    $message = "The committed AllyXLab.exe predates its source ($($drift.Count) files differ). Rebuild with eng\allyxlab-download.ps1 -Build when a tester needs it:`n  $(($drift | Sort-Object) -join "`n  ")"
    if ($FailOnSourceDrift) { throw $message }
    Write-Warning $message
}
else {
    Write-Host "Ally X Lab download matches BUILD.json and its source."
}
