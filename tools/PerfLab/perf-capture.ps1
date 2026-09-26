<#
.SYNOPSIS
    Records one performance scenario: an ETW trace plus per-second process counters, then summarizes it.

.DESCRIPTION
    Set the machine up for the scenario first (game running, overlay open, on battery), then run this
    script. It waits the countdown, records for the given number of seconds with Windows Performance
    Recorder (CPU sampling, context switches, .NET rundown, power), samples per-process counters once
    a second over WMI, collects the WSGM runtime counters with dotnet-counters when that tool is
    installed, and finally runs WSGM.PerfLab over the trace to write report.md.

    The raw trace is about 1 GB per 30 s and stays under the output directory, which defaults to
    %LOCALAPPDATA%\WSGM\perf. Copy report.md and counters.csv into docs/perf/captures when a run is
    worth keeping; never commit the .etl.

    Windows Performance Recorder needs administrator rights. The script re-launches itself elevated
    when it is not already, and the elevated run writes everything to files.

.PARAMETER Scenario
    Short name of the scenario, used in the output directory name, for example idle-desktop or
    in-game-overlay-open.

.PARAMETER Seconds
    Recording length. Thirty seconds is enough for an idle scenario; use sixty for in-game runs.

.PARAMETER Countdown
    Seconds to wait before recording starts, so the operator can switch to the game or open the
    overlay after starting the script.

.PARAMETER OutRoot
    Directory the run directory is created under.

.PARAMETER NoSymbols
    Skip symbol loading in the analyzer. Faster, but native frames stay as module+offset.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9-]*$')] [string] $Scenario,
    [int] $Seconds = 30,
    [int] $Countdown = 0,
    [string] $OutRoot = (Join-Path $env:LOCALAPPDATA 'WSGM\perf'),
    [switch] $NoSymbols
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $OutRoot "$stamp-$Scenario"

$principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argumentList = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
        '-Scenario', $Scenario, '-Seconds', $Seconds, '-Countdown', $Countdown, '-OutRoot', "`"$OutRoot`""
    )
    if ($NoSymbols) { $argumentList += '-NoSymbols' }
    Write-Host "Elevating for Windows Performance Recorder. Output goes to $runDir"
    $process = Start-Process -FilePath (Get-Process -Id $PID).Path -Verb RunAs -PassThru -Wait -ArgumentList $argumentList
    exit $process.ExitCode
}

New-Item -ItemType Directory -Force -Path $runDir | Out-Null
Start-Transcript -Path (Join-Path $runDir 'capture.log') -Force | Out-Null
try {
    $processNames = @('WSGM', 'WSGM.Launch', 'WSGM.PackagedLaunch', 'WSGM.LogonService', 'steam', 'steamwebhelper', 'RTSS', 'RTSSHooksLoader64', 'LHMDataProvider')

    # Environment record: what was running, on which power source, at which build.
    # The shell is the largest WSGM.exe; a medium-integrity launcher that started it elevated stays
    # around as a second, small one.
    $wsgm = Get-Process WSGM -ErrorAction SilentlyContinue | Sort-Object WorkingSet64 -Descending | Select-Object -First 1
    $battery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
    $system = [ordered]@{
        scenario = $Scenario
        started = (Get-Date).ToString('o')
        seconds = $Seconds
        machine = (Get-CimInstance Win32_ComputerSystem).Model
        os = (Get-CimInstance Win32_OperatingSystem).Version
        wsgmVersion = if ($wsgm) { $wsgm.MainModule.FileVersionInfo.FileVersion } else { $null }
        wsgmPath = if ($wsgm) { $wsgm.Path } else { $null }
        wsgmUptimeMinutes = if ($wsgm) { [math]::Round(((Get-Date) - $wsgm.StartTime).TotalMinutes, 1) } else { $null }
        powerSource = if ($battery) { if ($battery.BatteryStatus -eq 2) { 'ac' } else { 'battery' } } else { 'unknown' }
        batteryPercent = if ($battery) { $battery.EstimatedChargeRemaining } else { $null }
        processes = @(Get-Process | Where-Object { $processNames -contains $_.ProcessName } | ForEach-Object { "$($_.ProcessName):$($_.Id)" } | Sort-Object)
    }
    $system | ConvertTo-Json | Set-Content (Join-Path $runDir 'system.json') -Encoding utf8

    if ($Countdown -gt 0) {
        Write-Host "Recording starts in $Countdown s. Set up the scenario now."
        Start-Sleep -Seconds $Countdown
    }

    $etl = Join-Path $runDir 'trace.etl'
    Write-Host "Recording $Scenario for $Seconds s..."
    & wpr -start CPU -start DotNET -start Power -filemode
    if ($LASTEXITCODE -ne 0) { throw "wpr -start failed with $LASTEXITCODE" }

    $countersJob = $null
    if (Get-Command dotnet-counters -ErrorAction SilentlyContinue) {
        if ($wsgm) {
            $countersCsv = Join-Path $runDir 'runtime-counters.csv'
            $countersJob = Start-Job -ScriptBlock {
                param($id, $seconds, $csv)
                & dotnet-counters collect -p $id --refresh-interval 1 --format csv -o $csv --duration ([TimeSpan]::FromSeconds($seconds).ToString('hh\:mm\:ss')) 2>&1
            } -ArgumentList $wsgm.Id, $Seconds, $countersCsv
        }
    } else {
        Write-Host 'dotnet-counters is not installed (dotnet tool install -g dotnet-counters); skipping runtime counters.'
    }

    # Process counters every five seconds. WMI is language neutral, unlike Get-Counter paths, but a
    # query is not free: the provider host showed up at a third of a core when this sampled every
    # second with a per-thread query, so the sampling stays coarse and CPU and wakeups come from the
    # trace, where they are exact.
    $rows = New-Object System.Collections.Generic.List[object]
    # A second instance of a process is 'Name#1' in the performance counters.
    $filter = ($processNames | ForEach-Object { "Name = '$_' OR Name LIKE '$_#%'" }) -join ' OR '
    $stopAt = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $stopAt) {
        $t = Get-Date
        $procs = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter $filter
        $discharge = (Get-CimInstance -Namespace root/wmi BatteryStatus -ErrorAction SilentlyContinue | Select-Object -First 1)
        foreach ($p in $procs) {
            $rows.Add([pscustomobject]@{
                time = $t.ToString('HH:mm:ss')
                process = $p.Name
                pid = $p.IDProcess
                cpuPercent = $p.PercentProcessorTime
                privateBytesMB = [math]::Round($p.WorkingSetPrivate / 1MB, 1)
                workingSetMB = [math]::Round($p.WorkingSet / 1MB, 1)
                handles = $p.HandleCount
                threads = $p.ThreadCount
                dischargeMilliwatts = if ($discharge -and $discharge.Discharging) { $discharge.DischargeRate } else { $null }
            })
        }
        $remaining = ($stopAt - (Get-Date)).TotalMilliseconds
        $wait = [math]::Min(5000 - ((Get-Date) - $t).TotalMilliseconds, $remaining)
        if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
    }
    $rows | Export-Csv (Join-Path $runDir 'counters.csv') -NoTypeInformation -Encoding utf8

    & wpr -stop $etl
    if ($LASTEXITCODE -ne 0) { throw "wpr -stop failed with $LASTEXITCODE" }
    if ($countersJob) { Receive-Job -Job $countersJob -Wait -AutoRemoveJob | Out-Null }

    # Per-process summary of the counters so the numbers can be read without opening the CSV.
    $rows | Group-Object process | ForEach-Object {
        $g = $_.Group
        '{0,-22} cpu% avg {1,6:N1}  private MB {2,7:N1}  working set MB {3,7:N1}  handles {4,5:N0}  threads {5,3:N0}' -f $_.Name,
            ($g | Measure-Object cpuPercent -Average).Average,
            ($g | Measure-Object privateBytesMB -Average).Average,
            ($g | Measure-Object workingSetMB -Average).Average,
            ($g | Measure-Object handles -Average).Average,
            ($g | Measure-Object threads -Average).Average
    } | Tee-Object -FilePath (Join-Path $runDir 'counters-summary.txt')

    $analyzer = Join-Path $PSScriptRoot 'WSGM.PerfLab.csproj'
    $analyzerArgs = @($etl, '--out', (Join-Path $runDir 'report.md'), '--top', '20')
    if ($NoSymbols) { $analyzerArgs += '--no-symbols' }
    Write-Host 'Summarizing the trace...'
    & dotnet run --project $analyzer -c Release -- @analyzerArgs
    if ($LASTEXITCODE -ne 0) { throw "WSGM.PerfLab failed with $LASTEXITCODE" }
    Write-Host "Done: $runDir"
} finally {
    Stop-Transcript | Out-Null
}
