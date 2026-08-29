<#
.SYNOPSIS
    Fails the build when a JIT-only device assembly reaches the NativeAOT WSGM output.

.DESCRIPTION
    The device platform is deliberately split: WSGM stays NativeAOT and statically links the
    AOT-safe WSGM.Device.Sdk, while DeviceHost, Device Lab, and every plugin stay JIT so they can
    use System.Management/WMI, WinRT sensors, and an interactive keyboard hook.

    A ProjectReference is caught by the dependency-direction test in tests\WSGM.Tests. This script
    catches the other half: a transitively copied binary, a stray None/Content item, or a publish
    profile that stages a plugin beside WSGM.exe. Those produce no compile error at all - the file
    simply appears in the output directory - so only an output-directory check finds them.

.PARAMETER OutputDirectory
    A WSGM build or publish output directory to inspect.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    throw "Output directory not found: $OutputDirectory"
}

# Assembly name prefixes and exact names that must never ship beside WSGM.exe. WSGM.Device.Sdk
# is deliberately absent: it is the one device assembly WSGM is allowed to carry, and under
# NativeAOT it is linked into the image rather than copied anyway.
$forbidden = @(
    "WSGM.DeviceHost",
    "WSGM.DeviceLab",
    "wsgm-device",
    "WSGM.Device.Msi.",
    "System.Management",
    "Microsoft.Management.Infrastructure",
    "Microsoft.Windows.SDK.NET",
    "WinRT.Runtime",
    "HIDMaestro"
)

$binaries = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Include *.dll, *.exe

$violations = [System.Collections.Generic.List[object]]::new()
foreach ($binary in $binaries) {
    foreach ($name in $forbidden) {
        if ($binary.Name -like "$name*") {
            $violations.Add([pscustomobject]@{
                    File  = $binary.FullName.Substring($OutputDirectory.Length).TrimStart('\', '/')
                    Match = $name
                })
            break
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "JIT-only device assemblies found in the NativeAOT WSGM output:" -ForegroundColor Red
    foreach ($violation in $violations) {
        Write-Host ("  {0}  (matched '{1}')" -f $violation.File, $violation.Match)
    }
    throw "WSGM may only statically link WSGM.Device.Sdk. DeviceHost, Device Lab, plugins, and JIT/WMI/WinRT binaries publish to isolated directories."
}

Write-Host "NativeAOT WSGM output contains no JIT-only device assembly."
