# Dev-only deploy: publish WSGM and swap it into the local install WITHOUT running setup.
#
# The setup round-trip costs minutes for what is, on the dev box, a file copy. Steam must restart anyway so the injected bootstrap and any WSGM-defined
# SteamClient.System.* namespaces are rebuilt from scratch: a bridge left over from the previous
# build keeps running the OLD injected script until Steam restarts, and a fix then appears to do
# nothing (see docs\steam-cef.md).
#
# Order matters on restart: WSGM first, then Steam, so WSGM's patch synchronization is already
# watching when Steam's SharedJSContext appears.
#
# This script is for the attended dev loop only. It is not part of any release path, CI never
# calls it, and it deliberately does not touch WSGM.LogonService.exe (it changes rarely, and the
# service holds it). WSGM lives under %ProgramFiles%\WSGM\App, so the swap runs behind one
# elevation prompt. Handheld and GPU drivers are delivered with the application.
[CmdletBinding()]
param(
    # Skip the publish and swap whatever publish\App already holds, for iterating on the swap
    # itself or re-deploying a build that was just made.
    [switch]$SkipBuild,

    # Arguments WSGM is restarted with. On this machine the running mode is the shell; plain
    # WSGM.exe would open Settings instead.
    [ValidateNotNull()]
    [string[]]$WsgmArguments = @('--shell'),

    # Leave Steam and WSGM stopped after the swap instead of restarting them.
    [switch]$NoRestart,

    # Deploy to the maintainer's desktop (MS-7E16) instead of the reference Claw. The desktop runs
    # WSGM desktop-resident beside Explorer and has no device package, so this restarts WSGM with
    # --shell --desktop-resident unless -WsgmArguments is given.
    [switch]$Desktop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The shell may only run on the reference Claw, and the desktop-resident deploy only on the
# maintainer's desktop. This script restarts Steam and the live shell, so on any other machine it
# would be a takeover nobody offered. The board product is the same one-command identity check the
# root AGENTS.md mandates before any hardware work.
$board = (Get-CimInstance -ClassName Win32_BaseBoard).Product
$expectedBoard, $machine = if ($Desktop) { @('MS-7E16', 'EQS_RTX'), 'desktop' } else { @('MS-1T52'), 'reference Claw' }
if (-not ($expectedBoard | Where-Object { $board -like "*($_)" -or $board -eq $_ })) {
    throw "dev-deploy refused: this machine reports board '$board', not the $machine ($expectedBoard)."
}
if ($Desktop) {
    if (-not $PSBoundParameters.ContainsKey('WsgmArguments')) {
        $WsgmArguments = @('--shell', '--desktop-resident')
    }
}

$root = Split-Path -Parent $PSScriptRoot
$appPublish = Join-Path $root 'publish\App'
$appDirectory = Join-Path $env:ProgramFiles 'WSGM\App'
$pluginsRoot = Join-Path $env:ProgramFiles 'WSGM\Plugins'
$steamExe = 'C:\Program Files (x86)\Steam\steam.exe'

if (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'WSGM.exe'))) {
    throw "No installed WSGM at $appDirectory - run WSGM setup once first."
}

if (-not $SkipBuild) {
    Write-Host '== Publishing WSGM (self-contained JIT) ==' -ForegroundColor Cyan
    # Preserve the release build environment that build.ps1 uses for native dependencies.
    $env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    npm run steam-assets:check
    if ($LASTEXITCODE -ne 0) { throw 'Steam UI asset drift check failed' }
    # The publish copies whatever eng\build-viiper.ps1 last staged. A library built from another
    # VIIPER commit than the one checked out would otherwise be deployed without a word.
    $viiperStamp = Join-Path $root 'src\WSGM\Native\Viiper\libviiper.revision'
    $viiperHead = (git -C (Join-Path $root 'external\viiper') rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the external\viiper revision.' }
    $stagedViiper = if (Test-Path -LiteralPath $viiperStamp) {
        (Get-Content -LiteralPath $viiperStamp -TotalCount 1).Trim()
    } else { '' }
    if ($stagedViiper -ne $viiperHead.Trim()) {
        throw "The staged VIIPER library was built from '$stagedViiper', but external\viiper is at $($viiperHead.Trim()). Run eng\build-viiper.ps1 first."
    }
    dotnet publish (Join-Path $root 'src\WSGM\WSGM.csproj') -c Release -r win-x64 `
        -o $appPublish -m:1
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    # The launch wrapper is swapped in below, so publish it too, the way build.ps1 does; otherwise a
    # wrapper left in publish\App by an older build would be deployed beside the new WSGM.exe.
    $csproj = Get-Content -LiteralPath (Join-Path $root 'src\WSGM\WSGM.csproj') -Raw
    if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw 'No <Version> found in WSGM.csproj' }
    dotnet publish (Join-Path $root 'src\WSGM.Launch\WSGM.Launch.csproj') -c Release -r win-x64 `
        -o $appPublish "/p:Version=$($Matches[1])" -m:1
    if ($LASTEXITCODE -ne 0) { throw 'WSGM.Launch publish failed' }

    # The packaged-game launcher is swapped in below for the same reason: a generated Xbox shortcut
    # points at a fixed path, so an older build left there would be what the attended test runs.
    # The bridge first, as the release build does. The project compiles happily without it, so
    # skipping this deploys a launcher whose UWP route degrades at launch - which is exactly the
    # thing the attended test is trying to measure.
    & (Join-Path $root 'eng\build-uwp-bridge.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Overlay bridge build failed' }

    dotnet publish (Join-Path $root 'src\WSGM.PackagedLaunch\WSGM.PackagedLaunch.csproj') -c Release -r win-x64 `
        -o $appPublish "/p:Version=$($Matches[1])" -m:1
    if ($LASTEXITCODE -ne 0) { throw 'WSGM.PackagedLaunch publish failed' }
}

$newExe = Join-Path $appPublish 'WSGM.exe'
if (-not (Test-Path -LiteralPath $newExe)) {
    throw "No published WSGM.exe at $newExe - build first or drop -SkipBuild."
}
foreach ($required in 'LibGPUDriverInteract.dll', 'LibGPUDriverInteract-LICENSE.txt',
    'LibGPUDriverInteract-PROVENANCE.md', 'LibHandheld.dll', 'LibHandheld-LICENSE.txt',
    'LibHandheld-PROVENANCE.md') {
    if (-not (Test-Path -LiteralPath (Join-Path $appPublish $required) -PathType Leaf)) {
        throw "No published $required at $appPublish - build first or drop -SkipBuild."
    }
}

# Publish overlays an existing output directory. Retire only the former SDK files before
# enumerating the application payload so an old assembly cannot enter the new install.
foreach ($retiredFile in 'WSGM.Device.Sdk.dll', 'WSGM.Device.Sdk.pdb', 'WSGM.Device.Sdk.xml') {
    $retiredPath = [IO.Path]::GetFullPath((Join-Path $appPublish $retiredFile))
    if ([IO.Path]::GetDirectoryName($retiredPath) -cne [IO.Path]::GetFullPath($appPublish)) {
        throw "Retired SDK path escaped the publish App directory: $retiredPath"
    }
    if (Test-Path -LiteralPath $retiredPath -PathType Leaf) {
        Remove-Item -LiteralPath $retiredPath -Force
    }
}

$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
# Both wrappers own a live game session: WSGM.Launch holds an input lease and a job over the game
# tree, and WSGM.PackagedLaunch is the only thing keeping Steam's running state for a packaged title
# it is also containing. Replacing either underneath a running game is how a session gets stranded.
$wrappers = @(Get-Process -Name 'WSGM.Launch', 'WSGM.PackagedLaunch' -ErrorAction SilentlyContinue |
    Where-Object SessionId -eq $sessionId)
if ($wrappers.Count -ne 0) {
    $ids = ($wrappers.Id | Sort-Object) -join ', '
    throw "dev-deploy refused: a game launch wrapper is active in this session (PID $ids). Close the game normally and retry."
}

Write-Host '== Asking Steam to exit, then stopping WSGM ==' -ForegroundColor Cyan
$steamProcesses = @(Get-Process -Name 'steam' -ErrorAction SilentlyContinue |
    Where-Object SessionId -eq $sessionId)
if ($steamProcesses.Count -ne 0) {
    Start-Process 'steam://exit'
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 250
        $steamProcesses = @(Get-Process -Name 'steam' -ErrorAction SilentlyContinue |
            Where-Object SessionId -eq $sessionId)
    } while ($steamProcesses.Count -ne 0 -and $deadline.Elapsed -lt [TimeSpan]::FromSeconds(20))

    if ($steamProcesses.Count -ne 0) {
        throw 'dev-deploy refused: Steam did not exit normally within 20 seconds. Close it manually and retry.'
    }
}

# Stop WSGM the way setup does: signal the update exit event and wait, so every instance runs its
# own exit path (HidHide uncloak, Steam Input release, desktop posture) and exits clean, which the
# logon-service watchdog does not treat as a crash. The wait is setup's 44 half-second polls. A
# WSGM that does not answer in that time is force-stopped, and the fallback says so.
$wsgmProcesses = @(Get-Process -Name 'WSGM' -ErrorAction SilentlyContinue |
    Where-Object SessionId -eq $sessionId)
if ($wsgmProcesses.Count -ne 0) {
    $exitRequest = $null
    if ([Threading.EventWaitHandle]::TryOpenExisting('Local\WSGM.ExitForUpdate', [ref]$exitRequest)) {
        try { [void]$exitRequest.Set() } finally { $exitRequest.Dispose() }
        for ($poll = 0; $poll -lt 44 -and $wsgmProcesses.Count -ne 0; $poll++) {
            Start-Sleep -Milliseconds 500
            $wsgmProcesses = @(Get-Process -Name 'WSGM' -ErrorAction SilentlyContinue |
                Where-Object SessionId -eq $sessionId)
        }
    }
    if ($wsgmProcesses.Count -ne 0) {
        $ids = ($wsgmProcesses.Id | Sort-Object) -join ', '
        Write-Warning "WSGM (PID $ids) did not exit on the update request; force-stopping it. Its exit cleanup did not run."
        $wsgmProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        $wsgmProcesses | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

# What the setup payload would place in App: WSGM.exe, the launch wrappers and the managed and native
# libraries. The ShellAnchor is the same binary under the shell-registration name; leaving it stale
# would run two different builds in one session. WSGM.deps.json is in this list because the host
# reads it to decide what may be loaded at all. A swap that copies a new assembly but leaves the old
# dependency manifest produces the worst possible failure: the DLL is sitting in the directory and
# the runtime still reports "Could not load file or assembly", so every diagnostic points at a file
# that is plainly present. That is exactly what a dev-deploy did on the reference Claw on
# 2026-09-11, the first swap after WSGM.Plugin.Sdk became a project reference. runtimeconfig.json
# travels with it for the same reason: both describe the set that was just copied.
$copies = [Collections.Generic.List[object]]::new()
$copies.Add(@{ Source = $newExe; Name = 'WSGM.exe'; Process = '' })
$copies.Add(@{ Source = $newExe; Name = 'WSGM.ShellAnchor.exe'; Process = 'WSGM.ShellAnchor' })
foreach ($pattern in 'WSGM.Launch.exe', 'WSGM.PackagedLaunch.exe', '*.dll', 'WSGM.deps.json',
    'WSGM.runtimeconfig.json', 'LibGPUDriverInteract-LICENSE.txt', 'LibGPUDriverInteract-PROVENANCE.md',
    'LibHandheld-LICENSE.txt', 'LibHandheld-PROVENANCE.md', 'LibHandheld-Transports-NOTICES.md',
    'LibHandheld-Transports-MPL-2.0.txt', 'LibHandheld-Transports-LGPL-2.1.txt') {
    foreach ($file in @(Get-ChildItem -LiteralPath $appPublish -Filter $pattern -ErrorAction SilentlyContinue)) {
        $copies.Add(@{ Source = $file.FullName; Name = $file.Name; Process = '' })
    }
}

foreach ($name in 'Resources\Intel\KX\KX.exe', 'Resources\Intel\KX\kx.lock.json',
    'Resources\InpOut\inpoutx64.dll', 'Resources\InpOut\LICENSE.txt', 'Resources\InpOut\inpout.lock.json',
    'Resources\InpOut\UPSTREAM-README.txt') {
    $source = Join-Path $appPublish $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing handheld dependency $source" }
    $copies.Add(@{ Source = $source; Name = $name; Process = '' })
}

Write-Host "== Swapping files into $appDirectory (elevation required) ==" -ForegroundColor Cyan
$request = Join-Path $root 'publish\dev-deploy-request.json'
[ordered]@{
    SessionId = $sessionId
    AppDirectory = $appDirectory
    Copies = $copies
    PluginsRoot = $pluginsRoot
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $request -Encoding UTF8

# WSGM has exited by now, so a locked WSGM.exe fails the swap. A desktop session keeps a live anchor
# process (Explorer's launch parent) that holds its image; it is inert once Explorer is up, so it is
# stopped rather than left stale, and its copy retries briefly because a stopped process releases its
# image lock a beat after the process object dies. Retired first-party packages are removed by
# exact manifest identity; other packages and their state remain untouched.
$swap = @'
param([string]$RequestPath)
$ErrorActionPreference = 'Stop'
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
foreach ($retiredFile in 'WSGM.Device.Sdk.dll', 'WSGM.Device.Sdk.pdb', 'WSGM.Device.Sdk.xml') {
    $retiredPath = [IO.Path]::GetFullPath((Join-Path $request.AppDirectory $retiredFile))
    if ([IO.Path]::GetDirectoryName($retiredPath) -cne [IO.Path]::GetFullPath($request.AppDirectory)) {
        throw "Retired SDK path escaped the installed App directory: $retiredPath"
    }
    if (Test-Path -LiteralPath $retiredPath -PathType Leaf) {
        Remove-Item -LiteralPath $retiredPath -Force
    }
}

foreach ($copy in $request.Copies) {
    $target = Join-Path $request.AppDirectory $copy.Name
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
    for ($attempt = 1; ; $attempt++) {
        try {
            Copy-Item -LiteralPath $copy.Source -Destination $target -Force -ErrorAction Stop
            break
        } catch [System.IO.IOException] {
            if ($attempt -ge 10 -or -not $copy.Process) {
                throw "$($copy.Name) stayed locked through $attempt copy attempts."
            }
            Get-Process -Name $copy.Process -ErrorAction SilentlyContinue |
                Where-Object SessionId -eq $request.SessionId |
                Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
        }
    }
}
# The direct libraries replace only these built-in archive identities. Filename prefixes are
# insufficient: keep unrelated packages and all per-user DeviceState/PluginState journals.
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $request.PluginsRoot -PathType Container) {
    foreach ($package in @(Get-ChildItem -LiteralPath $request.PluginsRoot -File -Filter '*.wsgmpkg')) {
        if ($package.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $retired = $false
        try {
            $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
            try {
                $manifests = @($archive.Entries | Where-Object FullName -CEQ 'plugin.wsgm.json')
                if ($manifests.Count -eq 1 -and $manifests[0].Length -le 1MB) {
                    $reader = [IO.StreamReader]::new($manifests[0].Open())
                    try {
                        $manifest = $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop
                        $retired = $manifest -is [PSCustomObject] -and $manifest.id -is [string] -and
                            $manifest.id -cin @('wsgm.gpu.intel', 'wsgm.gpu.amd', 'wsgm.gpu.nvidia',
                                'wsgm.device.msi.claw', 'wsgm.device.asus.rog-ally')
                    } finally {
                        $reader.Dispose()
                    }
                }
            } finally {
                $archive.Dispose()
            }
        } catch [IO.InvalidDataException] {
            continue
        } catch [ArgumentException] {
            continue
        } catch [System.Management.Automation.RuntimeException] {
            # Invalid JSON or a missing identity cannot prove this is a retired package.
            continue
        }
        if ($retired) {
            Remove-Item -LiteralPath $package.FullName -Force
        }
    }
}
'@
$swapScript = Join-Path $root 'publish\dev-deploy-swap.ps1'
Set-Content -LiteralPath $swapScript -Value $swap -Encoding UTF8
$elevated = Start-Process -FilePath 'powershell.exe' `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$swapScript`"", '-RequestPath', "`"$request`"") `
    -Verb RunAs -WindowStyle Hidden -Wait -PassThru
if ($elevated.ExitCode -ne 0) {
    throw "Elevated swap failed (exit $($elevated.ExitCode))."
}
if ($NoRestart) {
    Write-Host 'Swap done; Steam and WSGM left stopped (-NoRestart).' -ForegroundColor Yellow
    return
}

Write-Host "== Starting WSGM $WsgmArguments, then Steam Big Picture ==" -ForegroundColor Cyan
Start-Process -FilePath (Join-Path $appDirectory 'WSGM.exe') -ArgumentList $WsgmArguments
# Shell mode holds Local\WSGM.Shell once it is up. The elevated shell's mutex refuses this unelevated
# script, and that refusal proves it exists as well as an open handle does.
if ($WsgmArguments -contains '--shell') {
    $startDeadline = [Diagnostics.Stopwatch]::StartNew()
    for (; ; ) {
        $shellMutex = $null
        try {
            if ([Threading.Mutex]::TryOpenExisting('Local\WSGM.Shell', [ref]$shellMutex)) {
                $shellMutex.Dispose()
                break
            }
        } catch [UnauthorizedAccessException] {
            break
        }
        if ($startDeadline.Elapsed -ge [TimeSpan]::FromSeconds(20)) {
            throw 'WSGM did not start its shell within 20 seconds of the swap - check %LOCALAPPDATA%\WSGM\wsgm.log.'
        }
        Start-Sleep -Milliseconds 250
    }
}
# Straight into Big Picture, the way WSGM cold-starts Steam itself (Steam.LaunchBigPicture). Starting
# Steam on the desktop and switching afterwards meant the switch had to be timed against Steam's own
# startup, and landing it early is what left the new build's first probes running against a
# half-built UI.
Start-Process -FilePath $steamExe -ArgumentList 'steam://open/bigpicture'
Write-Host 'Deployed.' -ForegroundColor Green
