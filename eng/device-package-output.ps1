# SPDX-License-Identifier: MIT
# Shared publication step for the MIT-licensed device package scripts.

function Publish-DevicePackageArchive {
    <#
    .SYNOPSIS
        Commits a staged archive without deleting the previous package first.
    .DESCRIPTION
        The caller validates output ownership and stages on the destination volume. Replacement
        is atomic; create-new publication refuses a destination created by another writer.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$StagedArchive,

        [Parameter(Mandatory)]
        [string]$Archive,

        [switch]$ReplaceExisting
    )

    if ($ReplaceExisting) {
        # PowerShell coerces $null to an empty string for this parameter; pass a real null instead.
        [IO.File]::Replace($StagedArchive, $Archive, [NullString]::Value)
    }
    else {
        [IO.File]::Move($StagedArchive, $Archive)
    }
}
