<#
.SYNOPSIS
    Regenerates Device Lab's extracted knowledge records from the decompiled Handheld Companion source.

.DESCRIPTION
    Reads _ref/HandheldCompanion (see its PROVENANCE.md), rewrites every hc.*.json under
    src/WSGM.DeviceLab/Knowledge/Devices, and formats them with Prettier. Curated records in the same
    directory are not touched. The output is committed; review the diff like any other change.
#>
param(
    [string]$Reference = "_ref/HandheldCompanion",
    [string]$HcVersion = "1.3.1.6"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$referenceRoot = Join-Path $root $Reference
$source = Join-Path $referenceRoot "source/HandheldCompanion"
$resources = Join-Path $referenceRoot "Resources/Devices"
$output = Join-Path $root "src/WSGM.DeviceLab/Knowledge/Devices"

foreach ($path in @($source, $resources)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Missing reference input: $path. Decompile Handheld Companion into $Reference first."
    }
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
dotnet run --project (Join-Path $root "tools/HcDeviceExtract/HcDeviceExtract.csproj") -c Release -- `
    --source $source --resources $resources --output $output --hc-version $HcVersion
if ($LASTEXITCODE -ne 0) {
    throw "HcDeviceExtract failed with exit code $LASTEXITCODE."
}

Push-Location $root
try {
    npx prettier --write "src/WSGM.DeviceLab/Knowledge/Devices/hc.*.json" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Prettier failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
