#requires -RunAsAdministrator
# Maintainer-authorized local Atlas maintenance. Not invoked by WSGM runtime or validation.
# Retires the \WSGM-Atlas-OemDisabledState guard: Acer delivers Atlas BIOS updates through Predator
# Sense for Handheld (2026-11-07), so nothing may keep its app, tasks, services or packages disabled.
# This only removes the guard. Restore the Acer targets from the local snapshots afterwards with
# artifacts/Restore-Acer-OemSoftware.ps1; the controller keyboard leaf stays as it is.
$ErrorActionPreference = 'Stop'
$taskName = 'WSGM-Atlas-OemDisabledState'
$folder = Join-Path $env:ProgramData 'WSGM\AtlasOemControl'
$installed = Join-Path $folder 'Keep-Acer-OemDisabled.ps1'

$task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
if ($task) {
    if ($task.State -eq 'Running') {
        Stop-ScheduledTask -TaskName $taskName -TaskPath '\'
    }
    Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false
    Write-Output "Unregistered \$taskName."
} else {
    Write-Output "\$taskName is not registered."
}

if (Test-Path -LiteralPath $installed) {
    Remove-Item -LiteralPath $installed
    Write-Output "Removed $installed."
}

[pscustomobject]@{ CheckedAt = [DateTimeOffset]::UtcNow; Retired = $true } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'last-check.json')
Write-Output 'Restore the Acer services, tasks and packages with artifacts/Restore-Acer-OemSoftware.ps1.'
