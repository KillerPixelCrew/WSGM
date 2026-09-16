# SPDX-License-Identifier: MIT
# Shared Device Lab publication for publish-device-lab.ps1 and stage-device-components.ps1.

function Publish-DeviceLab {
    <#
    .SYNOPSIS
        Publishes Device Lab self-contained, with its licence and the exact .NET runtime notices.
    .DESCRIPTION
        A self-contained publish redistributes the runtime, so the notices are copied out of the
        exact restored runtime pack rather than a checked-in copy, which would drift silently on
        every runtime bump. Every copied notice and the licence must be a regular file, never a link
        or reparse point. The caller owns the destination and checks what it requires afterwards.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Destination,

        [Parameter(Mandatory)]
        [string]$Configuration,

        [Parameter(Mandatory)]
        [string]$RuntimeIdentifier,

        # Passed to MSBuild when set; otherwise the project's own version applies.
        [string]$Version = "",

        [switch]$NoRestore
    )

    $deviceLabRoot = Join-Path $Root "src\WSGM.DeviceLab"
    $arguments = @(
        "publish",
        (Join-Path $deviceLabRoot "WSGM.DeviceLab.csproj"),
        "--configuration", $Configuration,
        "--runtime", $RuntimeIdentifier,
        "--self-contained", "true",
        "--output", $Destination
    )
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $arguments += "/p:Version=$Version"
    }
    # No symbols: the installer copies this tree recursively, and a .pdb carries build-machine
    # paths to users.
    $arguments += @(
        "/p:PublishSingleFile=false",
        "/p:TreatWarningsAsErrors=true",
        "/p:DebugType=none",
        "/p:CopyOutputSymbolsToPublishDirectory=false",
        "-m:1"
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing Device Lab failed."
    }

    # DebugType only covers the managed build. SkiaSharp and HarfBuzzSharp ship native symbols as
    # NuGet runtime assets (about 105 MB), which the publish copies regardless, so symbols are
    # removed afterwards, as pack-device.ps1 does for plugin packages.
    Get-ChildItem -LiteralPath $Destination -Filter "*.pdb" -File -Recurse | Remove-Item -Force

    # The runtime notices, taken from the pack this publish actually restored.
    $assetsPath = Join-Path $deviceLabRoot "obj\project.assets.json"
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Restore assets are missing: $assetsPath"
    }

    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -Depth 100
    $runtimePackName = "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier"
    $frameworks = @($assets.project.frameworks.psobject.Properties | ForEach-Object { $_.Value })
    $runtimeDependencies = @(
        $frameworks |
            ForEach-Object { $_.downloadDependencies } |
            Where-Object { [string]$_.name -ieq $runtimePackName }
    )
    if ($runtimeDependencies.Count -ne 1) {
        throw "Restore must resolve exactly one $runtimePackName pack."
    }

    # An exact pin, not a range: the notice must describe one specific redistributed runtime.
    $versionRange = ([string]$runtimeDependencies[0].version -replace '^\[|\]$', '')
    $bounds = @($versionRange.Split(',') | ForEach-Object { $_.Trim() })
    if ($bounds.Count -ne 2 -or $bounds[0] -cne $bounds[1] -or [string]::IsNullOrWhiteSpace($bounds[0])) {
        throw "Runtime pack version is not exact: $($runtimeDependencies[0].version)"
    }

    $runtimePack = $null
    foreach ($packageFolder in $assets.packageFolders.psobject.Properties.Name) {
        $candidate = Join-Path (Join-Path $packageFolder ($runtimePackName.ToLowerInvariant())) $bounds[0]
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $runtimePack = $candidate
            break
        }
    }
    if ($null -eq $runtimePack) {
        throw "Resolved runtime pack was not found in the restored package folders."
    }

    $copies = @(
        @{ Source = Join-Path $runtimePack "LICENSE.TXT"; Destination = "DotNetRuntime-LICENSE.txt" },
        @{ Source = Join-Path $runtimePack "THIRD-PARTY-NOTICES.TXT"; Destination = "DotNetRuntime-THIRD-PARTY-NOTICES.txt" },
        @{ Source = Join-Path $deviceLabRoot "LICENSE"; Destination = "LICENSE.txt" }
    )
    foreach ($copy in $copies) {
        if (-not (Test-Path -LiteralPath $copy.Source -PathType Leaf)) {
            throw "Required licence or notice file is missing: $($copy.Source)"
        }
        $item = Get-Item -LiteralPath $copy.Source
        if ($item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Licence and notice files may not be copied through a link or reparse point: $($copy.Source)"
        }
        Copy-Item -LiteralPath $copy.Source -Destination (Join-Path $Destination $copy.Destination) -Force
    }
}
