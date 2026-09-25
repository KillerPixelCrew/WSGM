# Dev-only deploy: publish WSGM and swap it into the local install WITHOUT running setup.
#
# The setup round-trip costs minutes for what is, on the dev box, a file copy. Steam must restart anyway so the injected bootstrap and any WSGM-defined
# SteamClient.System.* namespaces are rebuilt from scratch — a bridge left over from the previous
# build keeps running the OLD injected script until Steam restarts, and a fix then appears to do
# nothing (see docs\steam-cef.md).
#
# Order matters on restart: WSGM first, then Steam, so WSGM's patch synchronization is already
# watching when Steam's SharedJSContext appears.
#
# This script is for the attended dev loop only. It is not part of any release path, CI never
# calls it, and it deliberately does not touch WSGM.LogonService.exe (it changes rarely, and the
# service holds it). WSGM lives under %ProgramFiles%\WSGM\App, so the swap runs behind one
# elevation prompt. Unless -SkipPlugin or -Desktop is given, the same prompt also drops a freshly
# built Claw device package into %ProgramFiles%\WSGM\Plugins.
[CmdletBinding()]
param(
    # Skip the publish and swap whatever publish\App already holds — for iterating on the swap
    # itself or re-deploying a build that was just made.
    [switch]$SkipBuild,

    # Arguments WSGM is restarted with. On this machine the running mode is the shell; plain
    # WSGM.exe would open Settings instead.
    [ValidateNotNull()]
    [string[]]$WsgmArguments = @('--shell'),

    # Leave Steam and WSGM stopped after the swap instead of restarting them.
    [switch]$NoRestart,

    # Skip refreshing the installed device plugin. The plugin rebuild + one elevation prompt only
    # matter when the SDK or the built-in package changed; a pure WSGM code loop can skip both.
    [switch]$SkipPlugin,

    # Deploy to the maintainer's desktop (MS-7E16) instead of the reference Claw. The desktop runs
    # WSGM desktop-resident beside Explorer and has no device package, so this restarts WSGM with
    # --shell --desktop-resident unless -WsgmArguments is given, and implies -SkipPlugin.
    [switch]$Desktop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The shell may only run on the reference Claw, and the desktop-resident deploy only on the
# maintainer's desktop. This script restarts Steam and the live shell, so on any other machine it
# would be a takeover nobody offered. The board product is the same one-command identity check the
# root AGENTS.md mandates before any hardware work.
$board = (Get-CimInstance -ClassName Win32_BaseBoard).Product
$expectedBoard, $machine = if ($Desktop) { 'MS-7E16', 'desktop' } else { 'MS-1T52', 'reference Claw' }
if ($board -notlike "*($expectedBoard)" -and $board -ne $expectedBoard) {
    throw "dev-deploy refused: this machine reports board '$board', not the $machine ($expectedBoard)."
}
if ($Desktop) {
    $SkipPlugin = $true
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

# Stop until quiet, not once: the logon-service watchdog respawns WSGM right after a kill, and
# that respawn held WSGM.exe through the copy on three consecutive deploys (2026-09-01). A process
# may also exit between enumeration and Stop-Process, which is success, not an error.
$stopDeadline = [Diagnostics.Stopwatch]::StartNew()
do {
    $wsgmProcesses = @(Get-Process -Name 'WSGM' -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $sessionId)
    foreach ($process in $wsgmProcesses) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
        } catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            # Already gone — the watchdog's respawn can die on its own between the
            # enumeration and the stop.
        }
    }
    if ($wsgmProcesses.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
} while ($stopDeadline.Elapsed -lt [TimeSpan]::FromSeconds(10))

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
$copies.Add(@{ Source = $newExe; Name = 'WSGM.exe'; Process = 'WSGM' })
$copies.Add(@{ Source = $newExe; Name = 'WSGM.ShellAnchor.exe'; Process = 'WSGM.ShellAnchor' })
foreach ($pattern in 'WSGM.Launch.exe', 'WSGM.PackagedLaunch.exe', '*.dll', 'WSGM.deps.json',
    'WSGM.runtimeconfig.json') {
    foreach ($file in @(Get-ChildItem -LiteralPath $appPublish -Filter $pattern -ErrorAction SilentlyContinue)) {
        $copies.Add(@{ Source = $file.FullName; Name = $file.Name; Process = '' })
    }
}

$packageFile = ''
$packageId = ''
if (-not $SkipPlugin) {
    # The device plugin is a separate package file the App swap never touches, so a dev loop that
    # changes the SDK leaves a stale plugin the running host rejects as api-incompatible (device
    # features silently gone). Rebuild it from the device projects in this checkout exactly as the
    # release bundle does.
    Write-Host '== Packing the device plugin from WSGM source ==' -ForegroundColor Cyan
    $pluginStage = Join-Path $root 'publish\DevDeviceComponents'
    Remove-Item -LiteralPath $pluginStage -Recurse -Force -ErrorAction SilentlyContinue
    # The bundle script fails by throwing; $LASTEXITCODE after a script call only repeats its last
    # native command. A run that returns without a package is caught by the count below.
    & "$root\eng\build-bundle.ps1" -OutputRoot $pluginStage -Only 'wsgm.device.msi.claw-8-a2vm' `
        -SkipTools -SkipCommunity

    $packagesRoot = Join-Path $pluginStage 'Packages'
    $stagedPackage = @(Get-ChildItem -LiteralPath $packagesRoot -File -Filter '*.wsgmpkg')
    if ($stagedPackage.Count -ne 1) {
        throw "Expected exactly one staged package under $packagesRoot; found $($stagedPackage.Count)."
    }
    $packageFile = $stagedPackage[0].FullName
    $packageId = ($stagedPackage[0].BaseName -replace '-[0-9][0-9.]*$', '')
}

Write-Host "== Swapping files into $appDirectory (elevation required) ==" -ForegroundColor Cyan
$request = Join-Path $root 'publish\dev-deploy-request.json'
[ordered]@{
    SessionId = $sessionId
    AppDirectory = $appDirectory
    Copies = $copies
    PluginsRoot = $pluginsRoot
    PackageFile = $packageFile
    PackageId = $packageId
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $request -Encoding UTF8

# The exe copies retry briefly: a killed process releases its image lock a beat after the process
# object dies, and the watchdog respawn can hold it for a moment more. Each failed attempt stops the
# named process in this session again. A desktop session keeps a live anchor process (Explorer's
# launch parent) that holds its image; it is inert once Explorer is up, so it is stopped rather than
# left stale. The plugin is copied beside its target and renamed so the folder never holds a
# half-written package, then every other build of that id, which a dev deploy owns, is removed.
# Nothing else in the folder is touched.
$swap = @'
param([string]$RequestPath)
$ErrorActionPreference = 'Stop'
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
foreach ($copy in $request.Copies) {
    $target = Join-Path $request.AppDirectory $copy.Name
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
if ($request.PackageFile) {
    New-Item -ItemType Directory -Path $request.PluginsRoot -Force | Out-Null
    $target = Join-Path $request.PluginsRoot (Split-Path -Leaf $request.PackageFile)
    $incoming = "$target.incoming"
    Copy-Item -LiteralPath $request.PackageFile -Destination $incoming -Force
    Get-ChildItem -LiteralPath $request.PluginsRoot -File -Filter "$($request.PackageId)-*.wsgmpkg" |
        Where-Object { $_.FullName -ne $target } |
        Remove-Item -Force
    Move-Item -LiteralPath $incoming -Destination $target -Force
}
'@
$swapScript = Join-Path $root 'publish\dev-deploy-swap.ps1'
Set-Content -LiteralPath $swapScript -Value $swap -Encoding UTF8
$elevated = Start-Process -FilePath 'powershell.exe' `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$swapScript`"", '-RequestPath', "`"$request`"") `
    -Verb RunAs -Wait -PassThru
if ($elevated.ExitCode -ne 0) {
    throw "Elevated swap failed (exit $($elevated.ExitCode))."
}
if ($packageId) {
    Write-Host "Device plugin $packageId installed." -ForegroundColor Green
}

if ($NoRestart) {
    Write-Host 'Swap done; Steam and WSGM left stopped (-NoRestart).' -ForegroundColor Yellow
    return
}

Write-Host "== Starting WSGM $WsgmArguments, then Steam Big Picture ==" -ForegroundColor Cyan
Start-Process -FilePath (Join-Path $appDirectory 'WSGM.exe') -ArgumentList $WsgmArguments
Start-Sleep -Seconds 6
if (-not (Get-Process WSGM -ErrorAction SilentlyContinue)) {
    throw 'WSGM did not stay running after the swap - check %LOCALAPPDATA%\WSGM\wsgm.log.'
}
# Straight into Big Picture, the way WSGM cold-starts Steam itself (Steam.LaunchBigPicture). Starting
# Steam on the desktop and switching afterwards meant the switch had to be timed against Steam's own
# startup, and landing it early is what left the new build's first probes running against a
# half-built UI.
Start-Process -FilePath $steamExe -ArgumentList 'steam://open/bigpicture'
Write-Host 'Deployed.' -ForegroundColor Green
