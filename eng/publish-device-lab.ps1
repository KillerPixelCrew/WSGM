<#
.SYNOPSIS
    Publishes Device Lab as a self-contained win-x64 tree, with its licence notices.

.DESCRIPTION
    This is what a release ships and what WSGM builds from the same source checkout. The output
    is complete on its own: a machine with no .NET installed can run it, which is the point for a
    tool that inspects handhelds that are not development machines.

    The .NET runtime notices are copied out of the exact restored runtime pack rather than a
    checked-in copy. A self-contained publish redistributes that runtime, so the notice has to
    match the version actually embedded, and hardcoding it would drift silently on every bump.

    -OutputRoot must name a directory below publish\ or artifacts\ in this repository. It is cleared
    and recreated on every run; any other path is refused.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = "publish/DeviceLab",

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64",

    [string]$Version = "",

    # Publish one self-extracting wsgm-device.exe for a remote tester instead of the folder.
    [switch]$Portable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "device-lab-publish.ps1")
$project = Join-Path $root "src\WSGM.DeviceLab\WSGM.DeviceLab.csproj"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectText = Get-Content -LiteralPath $project -Raw
    if ($projectText -notmatch '<Version>([^<]+)</Version>') {
        throw "Device Lab project does not declare a version."
    }
    $Version = $Matches[1]
}
$destination = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $root $OutputRoot))
}

# The output is this script's own directory under publish\ or artifacts\, cleared and recreated.
$destination = Reset-OwnedOutput -Path $destination
Publish-DeviceLab -Root $root -Destination $destination -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier -Version $Version -Portable:$Portable

Write-Host "Device Lab published to $destination"
