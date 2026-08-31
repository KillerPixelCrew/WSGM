# Dev-only deploy: publish WSGM and swap it into the local install WITHOUT the installer.
#
# The installer round-trip costs minutes and a UAC prompt for what is, on the dev box, a file
# copy. Steam must restart anyway so the injected bootstrap and any WSGM-defined
# SteamClient.System.* namespaces are rebuilt from scratch — a bridge left over from the previous
# build keeps running the OLD injected script until Steam restarts, and a fix then appears to do
# nothing (see docs\steam-cef.md).
#
# Order matters: WSGM first, then Steam, so WSGM's patch synchronization is already watching when
# Steam's SharedJSContext appears.
#
# This script is for the attended dev loop only. It is not part of any release path, CI never
# calls it, and it deliberately does not touch WSGM.LogonService.exe (Program Files, elevation,
# and it changes rarely) or the plugin slot (administrator-owned, replaced only by the
# installer).
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
    [switch]$NoRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The shell may only run on the reference Claw. The maintainer develops on other machines where
# WSGM is not installed, and this script ends Explorer's replacement-in-waiting (it kills Steam and
# restarts the live shell) — on the wrong machine that is a takeover of a desktop nobody offered.
# The board product is the same one-command identity check the root AGENTS.md mandates before any
# hardware work.
$board = (Get-CimInstance -ClassName Win32_BaseBoard).Product
if ($board -ne 'MS-1T52') {
    throw "dev-deploy refused: this machine reports board '$board', not the reference Claw (MS-1T52)."
}

$root = Split-Path -Parent $PSScriptRoot
$appPublish = Join-Path $root 'publish\App'
$binDirectory = Join-Path $env:LOCALAPPDATA 'WSGM\bin'
$steamExe = 'C:\Program Files (x86)\Steam\steam.exe'

if (-not (Test-Path -LiteralPath $binDirectory)) {
    throw "No installed WSGM at $binDirectory - run the real installer once first."
}

if (-not $SkipBuild) {
    Write-Host '== Publishing WSGM (self-contained JIT) ==' -ForegroundColor Cyan
    # Preserve the release build environment that build.ps1 uses for native dependencies.
    $env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
    npm run steam-assets:verify
    if ($LASTEXITCODE -ne 0) { throw 'Steam UI asset drift check failed' }
    dotnet publish (Join-Path $root 'src\WSGM\WSGM.csproj') -c Release -r win-x64 `
        -o $appPublish -m:1
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}

$newExe = Join-Path $appPublish 'WSGM.exe'
if (-not (Test-Path -LiteralPath $newExe)) {
    throw "No published WSGM.exe at $newExe - build first or drop -SkipBuild."
}

Write-Host '== Stopping Steam and WSGM ==' -ForegroundColor Cyan
foreach ($name in 'steam', 'WSGM', 'WSGM.Launch') {
    try { Stop-Process -Name $name -Force -ErrorAction Stop } catch {
        # Not running is the normal case for at least one of these; anything else should be seen.
        if ($_.CategoryInfo.Category -ne 'ObjectNotFound') { Write-Error -ErrorRecord $_ }
    }
}
# A killed process releases its image mapping asynchronously; copying too early intermittently
# hits a locked file.
Start-Sleep -Seconds 3

Write-Host "== Swapping files into $binDirectory ==" -ForegroundColor Cyan
# WSGM.exe plus everything the publish stages beside it that the installer would also place in
# {app}: the launch wrapper and the native helper DLLs. The ShellAnchor is the same binary under
# the shell-registration name; leaving it stale would run two different builds in one session.
Copy-Item -LiteralPath $newExe -Destination (Join-Path $binDirectory 'WSGM.exe') -Force
$anchor = Join-Path $binDirectory 'WSGM.ShellAnchor.exe'
if (Test-Path -LiteralPath $anchor) {
    Copy-Item -LiteralPath $newExe -Destination $anchor -Force
}
foreach ($pattern in 'WSGM.Launch.exe', '*.dll') {
    Get-ChildItem -LiteralPath $appPublish -Filter $pattern -ErrorAction SilentlyContinue |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $binDirectory $_.Name) -Force
        }
}

if ($NoRestart) {
    Write-Host 'Swap done; Steam and WSGM left stopped (-NoRestart).' -ForegroundColor Yellow
    return
}

Write-Host "== Starting WSGM $WsgmArguments, then Steam ==" -ForegroundColor Cyan
Start-Process -FilePath (Join-Path $binDirectory 'WSGM.exe') -ArgumentList $WsgmArguments
Start-Sleep -Seconds 6
if (-not (Get-Process WSGM -ErrorAction SilentlyContinue)) {
    throw 'WSGM did not stay running after the swap - check %LOCALAPPDATA%\WSGM\wsgm.log.'
}
Start-Process -FilePath $steamExe
Write-Host 'Deployed.' -ForegroundColor Green
