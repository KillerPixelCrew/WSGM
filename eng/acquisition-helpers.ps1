Set-StrictMode -Version Latest

$WsgmAcquisitionOwnerMarkerName = '.wsgm-acquisition-owner.json'
$WsgmAcquisitionInventoryName = '.pinned-payload.json'
$WsgmAcquisitionStampName = '.pinned-version'

function Get-WsgmPayloadRelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Path
    )

    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

function Get-WsgmPayloadFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $metadata = @(
        $WsgmAcquisitionOwnerMarkerName,
        $WsgmAcquisitionInventoryName,
        $WsgmAcquisitionStampName)
    return @(
        Get-ChildItem -LiteralPath $Root -File -Force -Recurse |
            Where-Object {
                (Get-WsgmPayloadRelativePath -Root $Root -Path $_.FullName) -notin $metadata
            } |
            Sort-Object {
                Get-WsgmPayloadRelativePath -Root $Root -Path $_.FullName
            }
    )
}

function Assert-WsgmPayloadHasNoLinks {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $rootItem = Get-Item -LiteralPath $Root
    if (-not $rootItem.PSIsContainer) {
        throw "Acquisition destination is not a directory: $Root"
    }
    if ($rootItem.LinkType -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Acquisition destination may not be a link or reparse point: $Root"
    }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force -Recurse) {
        if ($item.LinkType -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Acquired payload may not contain a link or reparse point: $($item.FullName)"
        }
    }
}

function Test-WsgmDestinationOwner {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$OwnerId,

        [Parameter()]
        [string]$AssetSha256
    )

    $markerPath = Join-Path $Root $WsgmAcquisitionOwnerMarkerName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        return $false
    }
    $markerItem = Get-Item -LiteralPath $markerPath
    if ($markerItem.LinkType -or
        ($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        return $false
    }
    try {
        $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json -Depth 8
        if ([int]$marker.schemaVersion -ne 1 -or
            [string]$marker.owner -cne $OwnerId) {
            return $false
        }
        return [string]::IsNullOrWhiteSpace($AssetSha256) -or
            [string]$marker.assetSha256 -ceq $AssetSha256
    }
    catch {
        return $false
    }
}

function Assert-WsgmDestinationReplaceable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$OwnerId
    )

    if (-not (Test-Path -LiteralPath $Root)) {
        return
    }
    Assert-WsgmPayloadHasNoLinks -Root $Root
    if (Test-WsgmDestinationOwner -Root $Root -OwnerId $OwnerId) {
        return
    }
    throw "Refusing to replace '$Root': it is not owned by $OwnerId. Move it aside or choose an empty destination."
}

function Initialize-WsgmPayloadMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$OwnerId,

        [Parameter(Mandatory)]
        [string]$AssetSha256
    )

    foreach ($name in @(
        $WsgmAcquisitionOwnerMarkerName,
        $WsgmAcquisitionInventoryName,
        $WsgmAcquisitionStampName)) {
        if (Test-Path -LiteralPath (Join-Path $Root $name)) {
            throw "The acquired archive reserved metadata name '$name'."
        }
    }

    $entries = @(
        foreach ($file in Get-WsgmPayloadFiles -Root $Root) {
            [ordered]@{
                path = Get-WsgmPayloadRelativePath -Root $Root -Path $file.FullName
                bytes = [long]$file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
            }
        }
    )
    if ($entries.Count -eq 0) {
        throw 'The acquired payload is empty.'
    }

    $inventory = [ordered]@{
        schemaVersion = 1
        assetSha256 = $AssetSha256
        files = $entries
    }
    $inventory | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $Root $WsgmAcquisitionInventoryName) -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $Root $WsgmAcquisitionStampName) `
        -Value $AssetSha256 -Encoding ASCII -NoNewline

    # The owner marker is written last. Its presence means the payload and inventory were both
    # completed, and is the authority that permits a later run to move or delete this tree.
    [ordered]@{
        schemaVersion = 1
        owner = $OwnerId
        assetSha256 = $AssetSha256
    } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $Root $WsgmAcquisitionOwnerMarkerName) -Encoding UTF8
}

function Test-WsgmPayloadCache {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$OwnerId,

        [Parameter(Mandatory)]
        [string]$AssetSha256,

        [Parameter(Mandatory)]
        [scriptblock]$ValidatePayload
    )

    try {
        if (-not (Test-WsgmDestinationOwner `
            -Root $Root `
            -OwnerId $OwnerId `
            -AssetSha256 $AssetSha256)) {
            return $false
        }
        Assert-WsgmPayloadHasNoLinks -Root $Root
        $stampPath = Join-Path $Root $WsgmAcquisitionStampName
        $inventoryPath = Join-Path $Root $WsgmAcquisitionInventoryName
        if (-not (Test-Path -LiteralPath $stampPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $inventoryPath -PathType Leaf) -or
            (Get-Content -LiteralPath $stampPath -Raw).Trim() -cne $AssetSha256) {
            return $false
        }

        $inventory = Get-Content -LiteralPath $inventoryPath -Raw |
            ConvertFrom-Json -Depth 16
        if ([int]$inventory.schemaVersion -ne 1 -or
            [string]$inventory.assetSha256 -cne $AssetSha256) {
            return $false
        }
        $expectedFiles = @($inventory.files)
        $actualFiles = @(Get-WsgmPayloadFiles -Root $Root)
        if ($expectedFiles.Count -eq 0 -or $expectedFiles.Count -ne $actualFiles.Count) {
            return $false
        }

        $rootPrefix = $Root.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $expectedPaths = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($expected in $expectedFiles) {
            $relative = [string]$expected.path
            if ([string]::IsNullOrWhiteSpace($relative) -or
                [IO.Path]::IsPathRooted($relative) -or
                $relative.Split('/', [StringSplitOptions]::RemoveEmptyEntries) -contains '..' -or
                -not $expectedPaths.Add($relative)) {
                return $false
            }
            $path = [IO.Path]::GetFullPath((Join-Path $Root $relative))
            if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                return $false
            }
            $file = Get-Item -LiteralPath $path
            if ([long]$file.Length -ne [long]$expected.bytes -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant() `
                    -cne [string]$expected.sha256) {
                return $false
            }
        }
        foreach ($actual in $actualFiles) {
            $relative = Get-WsgmPayloadRelativePath -Root $Root -Path $actual.FullName
            if (-not $expectedPaths.Contains($relative)) {
                return $false
            }
        }

        & $ValidatePayload $Root
        return $true
    }
    catch {
        Write-Verbose "Cached payload validation failed: $($_.Exception.Message)"
        return $false
    }
}

function Install-WsgmPayloadAtomically {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StagingRoot,

        [Parameter(Mandatory)]
        [string]$DestinationRoot,

        [Parameter(Mandatory)]
        [string]$OwnerId
    )

    $stagingFull = [IO.Path]::GetFullPath($StagingRoot)
    $destinationFull = [IO.Path]::GetFullPath($DestinationRoot)
    if ($stagingFull.Equals($destinationFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Acquisition staging and destination must be distinct directories.'
    }

    Assert-WsgmPayloadHasNoLinks -Root $stagingFull
    if (-not (Test-WsgmDestinationOwner -Root $stagingFull -OwnerId $OwnerId)) {
        throw "Refusing to install an unowned acquisition staging tree: $stagingFull"
    }
    Assert-WsgmDestinationReplaceable -Root $destinationFull -OwnerId $OwnerId

    $stagingParentText = Split-Path -Path $stagingFull -Parent
    $parentText = Split-Path -Path $destinationFull -Parent
    $leaf = Split-Path -Path $destinationFull -Leaf
    if ([string]::IsNullOrWhiteSpace($stagingParentText) -or
        [string]::IsNullOrWhiteSpace($parentText) -or
        [string]::IsNullOrWhiteSpace($leaf)) {
        throw "Acquisition destination must be a named directory below an existing root: $destinationFull"
    }
    $stagingParent = [IO.Path]::GetFullPath($stagingParentText)
    $parent = [IO.Path]::GetFullPath($parentText)
    if (-not $stagingParent.Equals($parent, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Acquisition staging must be a unique sibling of its destination.'
    }
    $backupRoot = Join-Path $parent (
        '.{0}.backup-{1}-{2}' -f $leaf, $PID, [Guid]::NewGuid().ToString('N'))
    $backupCreated = $false
    try {
        if (Test-Path -LiteralPath $destinationFull) {
            [IO.Directory]::Move($destinationFull, $backupRoot)
            $backupCreated = $true
            # Re-check the moved object, not just the name checked before the move. If the caller
            # replaced the path between validation and Directory.Move, roll that directory back
            # rather than ever deleting it as though it were our cache.
            Assert-WsgmDestinationReplaceable -Root $backupRoot -OwnerId $OwnerId
        }
        [IO.Directory]::Move($stagingFull, $destinationFull)
    }
    catch {
        $replacementFailure = $_.Exception
        if ($backupCreated) {
            try {
                if (Test-Path -LiteralPath $destinationFull) {
                    throw "The destination was recreated while replacement was in progress: $destinationFull"
                }
                [IO.Directory]::Move($backupRoot, $destinationFull)
                $backupCreated = $false
            }
            catch {
                throw "Acquisition replacement failed: $($replacementFailure.Message) Rollback also failed: $($_.Exception.Message) The previous payload remains at '$backupRoot'."
            }
        }
        throw $replacementFailure
    }

    if ($backupCreated) {
        try {
            Remove-Item -LiteralPath $backupRoot -Recurse -Force
        }
        catch {
            Write-Warning "The previous owned payload remains at '$backupRoot': $($_.Exception.Message)"
        }
    }
}
