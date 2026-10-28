#requires -RunAsAdministrator
# Maintainer-authorized local Atlas maintenance. Not invoked by WSGM runtime or validation.
$ErrorActionPreference = 'Stop'
$services = @('AASHHSvc','AcerCCAgentSvis','AcerDeviceEnablingServiceV2','AcerHandheld.Service','AcerLightingServiceHH','AcerORDService','AcerServiceSvc','AFZsvc','ALSSvc','UEIPSvc')
$tasks = @('PredatorHandheldLauncher','UEIPInvitation','UbtFrameworkService')
$packages = @('ULICTekInc.PredatorSenseforHandHeld','ULICTekInc.PredatorSenseforHandHeldWidget')
$changes = @()
# The health record lives with the installed task, never beside the tracked copy in the repository.
$record = Join-Path $env:ProgramData 'WSGM\AtlasOemControl\last-check.json'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $record) | Out-Null
try {
    $system = Get-CimInstance Win32_ComputerSystem
    $board = Get-CimInstance Win32_BaseBoard
    if ($system.Manufacturer.Trim() -ne 'Acer' -or $system.Model.Trim() -ne 'Predator PA08-I51' -or
        $board.Manufacturer.Trim() -ne 'PTL' -or $board.Product.Trim() -ne 'F900_PTG') {
        throw 'This maintenance policy only admits the measured Acer Atlas 8 PA08-I51.'
    }
    foreach ($service in Get-CimInstance Win32_Service | Where-Object { $_.Name -in $services }) {
        if ($service.StartMode -ne 'Disabled') {
            Set-Service -Name $service.Name -StartupType Disabled
            $changes += "Disabled service $($service.Name)"
        }
        if ($service.State -ne 'Stopped') {
            Stop-Service -Name $service.Name
            $changes += "Stopped service $($service.Name)"
        }
    }
    foreach ($task in Get-ScheduledTask | Where-Object { $_.TaskPath -eq '\' -and $_.TaskName -in $tasks }) {
        if ($task.Settings.Enabled) {
            Disable-ScheduledTask -TaskName $task.TaskName -TaskPath '\' | Out-Null
            $changes += "Disabled task $($task.TaskName)"
        }
    }
    $manager = [Windows.Management.Deployment.PackageManager,Windows.Management.Deployment,ContentType=WindowsRuntime]::new()
    $disabled = [Windows.Management.Deployment.PackageStatus,Windows.Management.Deployment,ContentType=WindowsRuntime]::Disabled
    foreach ($package in Get-AppxPackage | Where-Object { $_.Name -in $packages }) {
        if (([int]$package.Status -band 8) -eq 0) {
            $manager.SetPackageStatus($package.PackageFullName, $disabled)
            $changes += "Disabled package $($package.PackageFullName)"
        }
        $executable = if ($package.Name -eq 'ULICTekInc.PredatorSenseforHandHeldWidget') { 'Handheld.Widget.exe' } else { 'Handheld.Desktop\Handheld.Desktop.exe' }
        $expected = Join-Path $package.InstallLocation $executable
        $processName = [IO.Path]::GetFileNameWithoutExtension($executable)
        foreach ($process in Get-Process -Name $processName -ErrorAction SilentlyContinue) {
            if ($process.Path -eq $expected) {
                Stop-Process -Id $process.Id
                $changes += "Stopped package process $processName"
            }
        }
    }
    [pscustomobject]@{ CheckedAt=[DateTimeOffset]::UtcNow; Healthy=$true; Changes=$changes } |
        ConvertTo-Json | Set-Content -LiteralPath $record
} catch {
    [pscustomobject]@{ CheckedAt=[DateTimeOffset]::UtcNow; Healthy=$false; Changes=$changes; Error=$_.Exception.Message } |
        ConvertTo-Json | Set-Content -LiteralPath $record
    exit 1
}
