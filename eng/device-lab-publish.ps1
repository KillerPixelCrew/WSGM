# SPDX-License-Identifier: MIT
# Shared Device Lab publication for publish-device-lab.ps1 and build-bundle.ps1.
. (Join-Path $PSScriptRoot "build-common.ps1")

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

        [switch]$NoRestore,

        # One self-extracting executable for testers, instead of the installer's folder.
        [switch]$Portable
    )

    # The wizard embeds the pinned PawnIO installer when it is present. A portable tester build exists
    # to be sent to someone, so it must carry it; the installer tree only warns when the download is
    # unavailable, and that Device Lab then reports PawnIO as unavailable instead of installing it.
    try {
        & (Join-Path $Root "eng\acquire-pawnio.ps1")
    }
    catch {
        if ($Portable) {
            throw
        }
        Write-Warning "PawnIO installer not acquired ($($_.Exception.Message)); this Device Lab build cannot install PawnIO."
    }

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
    # A portable tester build is one self-extracting file, native libraries included.
    $arguments += if ($Portable) {
        @(
            "/p:PublishSingleFile=true",
            "/p:IncludeNativeLibrariesForSelfExtract=true",
            "/p:EnableCompressionInSingleFile=true"
        )
    }
    else {
        @("/p:PublishSingleFile=false")
    }
    # No symbols: the installer copies this tree recursively, and a .pdb carries build-machine
    # paths to users.
    $arguments += @(
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

    Copy-RuntimeNotices -AssetsPath (Join-Path $deviceLabRoot "obj\project.assets.json") `
        -RuntimeIdentifier $RuntimeIdentifier -Destination $Destination

    $copies = @(
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
