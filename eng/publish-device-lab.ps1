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
$resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destination = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $resolvedRoot $OutputRoot))
}
$markerName = ".wsgm-devicelab-publish-root"
$markerValue = "WSGM.DeviceLab publish output v1"

function Assert-SafePublishPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $resolved)
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative -eq "." -or
        [IO.Path]::IsPathRooted($relative) -or $relative -eq ".." -or
        $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal)) {
        throw "Publish output must be a dedicated child of the WSGM repository: $resolved"
    }

    $current = Split-Path -Parent $resolved
    while (-not [string]::IsNullOrWhiteSpace($current) -and
        $current.StartsWith($RepositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Publish output ancestry cannot contain a reparse point: $current"
            }
        }
        if ($current -ieq $RepositoryRoot) {
            break
        }
        $current = Split-Path -Parent $current
    }

    return $resolved
}

function Test-TreeContainsReparsePoint {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                return $true
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            }
        }
    }
    return $false
}

$destination = Assert-SafePublishPath -Path $destination -RepositoryRoot $resolvedRoot
$destinationParent = Split-Path -Parent $destination
$destinationLeaf = Split-Path -Leaf $destination
$staging = Join-Path $destinationParent ".$destinationLeaf.$([Guid]::NewGuid().ToString('N')).tmp"
$staging = Assert-SafePublishPath -Path $staging -RepositoryRoot $resolvedRoot
$backup = $null


if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw "Publish destination exists and is not a directory: $destination"
    }
    $destinationItem = Get-Item -LiteralPath $destination -Force
    if (($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Publish destination cannot be a reparse point: $destination"
    }
    if (Test-TreeContainsReparsePoint -Path $destination) {
        throw "Publish destination contains a reparse point and will not be replaced: $destination"
    }
    $marker = Join-Path $destination $markerName
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        (Get-Content -LiteralPath $marker -Raw).Trim() -cne $markerValue) {
        throw "Refusing to replace an unowned directory. Remove it manually or choose a new -OutputRoot: $destination"
    }
}

try {
    [IO.Directory]::CreateDirectory($destinationParent) | Out-Null
    [IO.Directory]::CreateDirectory($staging) | Out-Null

    Publish-DeviceLab -Root $root -Destination $staging -Configuration $Configuration `
        -RuntimeIdentifier $RuntimeIdentifier -Version $Version -Portable:$Portable

Set-Content -LiteralPath (Join-Path $staging $markerName) `
    -Value $markerValue -NoNewline -Encoding UTF8
[IO.File]::SetAttributes(
    (Join-Path $staging $markerName),
    [IO.FileAttributes]::Hidden)

if (Test-TreeContainsReparsePoint -Path $staging) {
    throw "Publish staging unexpectedly contains a reparse point: $staging"
}

if (Test-Path -LiteralPath $destination) {
    if (Test-TreeContainsReparsePoint -Path $destination) {
        throw "Publish destination changed to contain a reparse point and will not be replaced: $destination"
    }
    $backup = Join-Path $destinationParent ".$destinationLeaf.$([Guid]::NewGuid().ToString('N')).backup"
    $backup = Assert-SafePublishPath -Path $backup -RepositoryRoot $resolvedRoot
    Move-Item -LiteralPath $destination -Destination $backup
}
Move-Item -LiteralPath $staging -Destination $destination

if ($null -ne $backup) {
    try {
        if (Test-TreeContainsReparsePoint -Path $backup) {
            throw "Previous publish changed to contain a reparse point: $backup"
        }
        Remove-Item -LiteralPath $backup -Recurse -Force
        $backup = $null
    } catch {
        Write-Warning "The new publish is complete, but the previous owned publish could not be removed: $backup"
    }
}
} catch {
    $publishFailure = $_
    if ($null -ne $backup -and
        -not (Test-Path -LiteralPath $destination) -and
        (Test-Path -LiteralPath $backup -PathType Container)) {
        try {
            $backupMarker = Join-Path $backup $markerName
            if ((Get-Item -LiteralPath $backup -Force).Attributes -band [IO.FileAttributes]::ReparsePoint -or
                (Test-TreeContainsReparsePoint -Path $backup) -or
                -not (Test-Path -LiteralPath $backupMarker -PathType Leaf) -or
                (Get-Content -LiteralPath $backupMarker -Raw).Trim() -cne $markerValue) {
                throw "The previous publish backup failed its ownership check: $backup"
            }
            Move-Item -LiteralPath $backup -Destination $destination
            $backup = $null
        } catch {
            Write-Warning "Publish replacement failed and the previous owned publish could not be restored: $backup"
        }
    }
    try {
        if (Test-Path -LiteralPath $staging -PathType Container) {
            $stagingItem = Get-Item -LiteralPath $staging -Force
            if (($stagingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
                (Split-Path -Parent $staging) -ieq $destinationParent -and
                (Split-Path -Leaf $staging).StartsWith(".$destinationLeaf.", [StringComparison]::Ordinal) -and
                -not (Test-TreeContainsReparsePoint -Path $staging)) {
                Remove-Item -LiteralPath $staging -Recurse -Force
            }
        }
    } catch {
        Write-Warning "Publish staging cleanup failed: $staging"
    }
    throw $publishFailure
}

Write-Host "Device Lab published to $destination"
