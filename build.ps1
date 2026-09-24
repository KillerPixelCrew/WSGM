# WSGM release build: self-contained publish, plugin bundle and the WSGM setup.
# Output: publish\WSGM-Setup-<version>.exe (the one-file setup, which carries the application,
# the virtual-controller stack and every bundled plugin; the only shipped artifact) and
# publish\bundle.json.
#
# -BundleFrom takes the plugin bundle (Packages, bundle.json, Tools) from a directory that
# eng\build-bundle.ps1 produced elsewhere. The release workflow builds the bundle in a job without
# secrets, because it compiles community plugin source, and hands it to this build.
param(
    [string]$BundleFrom = ""
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# The csproj <Version> is the single source of truth; WSGM.Setup reads it from there too.
$csproj = Get-Content "$root\src\WSGM\WSGM.csproj" -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "No <Version> found in WSGM.csproj" }
$version = $Matches[1]
# The app manifest identity must name the same version, or a hand-built setup ships metadata that
# disagrees with itself.
& "$root\eng\check-version-sync.ps1"

# This check rebuilds the asset from its TypeScript source and compares, so stale generated Steam
# UI code fails immediately. Install exactly the dependency graph in package-lock.json first: a
# release build must work from a clean checkout and must not reuse an unreviewed node_modules tree.
Write-Host "== Restoring locked Node.js tools ==" -ForegroundColor Cyan
Push-Location $root
try {
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }

    Write-Host "== Validating release inputs ==" -ForegroundColor Cyan
    npm run steam-assets:check
    if ($LASTEXITCODE -ne 0) { throw "Steam UI asset drift check failed" }
}
finally {
    Pop-Location
}

# The Steam Input gate is built from the source in external\steam-input-lease on every
# release build, so a shipped installer can never carry a gate older than the
# code beside it. This must precede the publish, which copies the staged output.
Write-Host "== Building Steam Input Lease (Rust) ==" -ForegroundColor Cyan
# -Validate for the export check: build.rs now drives exports from one authoritative
# .def, and the dumpbin ordinal comparison is the ONLY thing that catches link.exe
# putting an unrelated symbol at XInput's ordinal 104/109 - the stack-corruption case
# that .def exists to prevent. Without this the shipped DLL is the one artifact never
# export-checked, since eng\verify.ps1 only validates a separately built copy.
& "$root\eng\build-steam-input-lease.ps1" -Validate

# The virtual controller library is built from the external\viiper submodule. Controller management
# is a shipped feature, so a release without the library is an incomplete release, not a valid
# feature-local fallback artifact.
Write-Host "== Building virtual controller library (Go) ==" -ForegroundColor Cyan
& "$root\eng\build-viiper.ps1" -Validate -RequirePinned

Write-Host "== Publishing WSGM $version (self-contained JIT) ==" -ForegroundColor Cyan
# Clean first: dotnet publish overlays onto the previous output, so a DLL removed by
# a dependency bump (or an old setup exe) would otherwise leak into the release.
# Test-Path covers the only tolerable failure (no previous output); a clean that
# fails for any other reason must stop the build, not leak a stale tree.
if (Test-Path "$root\publish") { Remove-Item -Recurse -Force "$root\publish" }
$appPublish = "$root\publish\App"
New-Item -ItemType Directory -Path $appPublish | Out-Null

# One RID-aware restore feeds every --no-restore publish below.
dotnet restore "$root\WSGM.slnx" --runtime win-x64 -m:1
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

dotnet publish "$root\src\WSGM\WSGM.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore -m:1
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The user-facing Steam launch wrapper. Steam inherits WSGM's elevation, so this
# hands the real command to a medium-integrity scheduled-task child and/or holds a
# Steam Input block lease for the game's lifetime. Publish it beside WSGM so both
# portable and installed layouts use the same stable command path.
dotnet publish "$root\src\WSGM.Launch\WSGM.Launch.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore "/p:Version=$version" -m:1
if ($LASTEXITCODE -ne 0) { throw "WSGM.Launch publish failed" }

Write-Host "== Building the packaged-game overlay bridge (C++/MinHook) ==" -ForegroundColor Cyan
& "$root\eng\build-uwp-bridge.ps1" -Validate
if ($LASTEXITCODE -ne 0) { throw "Overlay bridge build failed" }

# The packaged-game launcher Steam starts for an imported Xbox, UWP or MSIX title. It stays alive
# for the session so Steam keeps the shortcut running, because package activation puts the game
# outside Steam's launch tree. Published beside WSGM so a generated shortcut has a stable target.
dotnet publish "$root\src\WSGM.PackagedLaunch\WSGM.PackagedLaunch.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore "/p:Version=$version" -m:1
if ($LASTEXITCODE -ne 0) { throw "WSGM.PackagedLaunch publish failed" }

# The SYSTEM logon service that launches WSGM's boot cover at sign-in. Published
# beside the rest; the installer ships it to Program Files (never user-writable).
dotnet publish "$root\src\WSGM.LogonService\WSGM.LogonService.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore "/p:Version=$version" -m:1
if ($LASTEXITCODE -ne 0) { throw "WSGM.LogonService publish failed" }

if (-not (Test-Path "$appPublish\WSGM.Launch.exe")) { throw "Launch wrapper was not produced" }
if (-not (Test-Path "$appPublish\WSGM.PackagedLaunch.exe")) { throw "Packaged-game launcher was not produced" }
if (-not (Test-Path "$appPublish\WsgmUwpBridge.dll")) { throw "Overlay bridge was not produced" }
if (-not (Test-Path "$appPublish\WSGM.LogonService.exe")) { throw "Logon service was not produced" }
if (-not (Test-Path "$appPublish\libviiper.dll")) { throw "VIIPER controller library was not published" }

# The USB/IP driver installer the virtual controller attaches through. It is a third-party asset
# fetched from its pinned release and verified here — on the release machine — against the reviewed
# digest and signer, so the copy the installer ships has already been checked by the time a user's
# setup re-checks it. Both usbip-win2 and HidHide are required payloads of the optional controller
# installer component; the release build fails rather than publishing a component that cannot work.
Write-Host "== Staging controller driver installers ==" -ForegroundColor Cyan
& "$root\eng\acquire-controller-dependencies.ps1" -Destination $appPublish

if ([string]::IsNullOrWhiteSpace($BundleFrom)) {
    Write-Host "== Building the plugin bundle ==" -ForegroundColor Cyan
    & "$root\eng\build-bundle.ps1" `
        -OutputRoot "$root\publish" `
        -Configuration Release `
        -RuntimeIdentifier win-x64 `
        -SkipTools `
        -NoRestore
}
else {
    Write-Host "== Taking the plugin bundle from $BundleFrom ==" -ForegroundColor Cyan
    foreach ($component in @("Packages", "bundle.json")) {
        Copy-Item -LiteralPath (Join-Path $BundleFrom $component) -Destination "$root\publish" -Recurse
    }
}

# The setup payload is an explicit allowlist, the same one the Inno installer shipped: App is what
# every install gets, Controller is what setup adds only when the installed plugin declares a
# controller role (VIIPER, the USB/IP driver and HidHide), Packages and bundle.json are every
# bundled plugin. Anything else in publish\App stays out.
Write-Host "== Assembling the setup payload ==" -ForegroundColor Cyan
$payload = "$root\publish\Payload"
$payloadApp = "$payload\App"
$payloadController = "$payload\Controller"
New-Item -ItemType Directory -Path $payloadApp, $payloadController | Out-Null
$appFiles = @(
    "WSGM.exe", "WSGM.deps.json", "WSGM.runtimeconfig.json", "WSGM.Launch.exe",
    "WSGM.PackagedLaunch.exe", "WsgmUwpBridge.dll", "MinHook-LICENSE.txt", "WSGM.LogonService.exe",
    "LICENSE.txt", "LoadingIndicators.Avalonia-UNLICENSE.txt", "Avalonia.Labs-MIT.txt",
    "Avalonia.LiveBackdrop.ThirdParty.txt"
)
foreach ($file in $appFiles) {
    Copy-Item -LiteralPath "$appPublish\$file" -Destination $payloadApp
}
# The Explorer recovery owner is the same image under a distinct name, so a force stop of WSGM.exe
# never ends the process that must restore Explorer.
Copy-Item -LiteralPath "$appPublish\WSGM.exe" -Destination "$payloadApp\WSGM.ShellAnchor.exe"
Get-ChildItem -LiteralPath $appPublish -File | Where-Object {
    ($_.Extension -eq ".dll" -and $_.Name -ne "libviiper.dll") -or $_.Name -like "SteamInputLease-*"
} | Copy-Item -Destination $payloadApp
foreach ($file in @("libviiper.dll", "libviiper.h", "VIIPER-LICENSE.txt", "VIIPER-NOTICE.md",
        "USBip-0.9.8.0-x64.exe", "HidHide_1.5.230_x64.exe")) {
    Copy-Item -LiteralPath "$appPublish\$file" -Destination $payloadController
}
Copy-Item -LiteralPath "$root\src\WSGM.Setup\Install-UsbipDriver.ps1" -Destination $payloadController
Copy-Item -LiteralPath "$root\external\controller\licenses\usbip-win2-BSD-2-Clause.txt" -Destination $payloadController
Copy-Item -LiteralPath "$root\external\controller\licenses\HidHide-MIT.txt" -Destination $payloadController
Copy-Item -LiteralPath "$root\publish\Packages" -Destination "$payload\Packages" -Recurse
Copy-Item -LiteralPath "$root\publish\bundle.json" -Destination $payload
& "$root\eng\assert-component-staging.ps1" -OutputRoot $payload

$payloadZip = "$root\publish\payload.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $payloadZip, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host "== Publishing WSGM.Setup ==" -ForegroundColor Cyan
$setupOut = "$root\publish\SetupOut"
dotnet publish "$root\src\WSGM.Setup\WSGM.Setup.csproj" -c Release -r win-x64 `
    -o $setupOut --no-restore "-p:SetupPayload=$payloadZip" -m:1
if ($LASTEXITCODE -ne 0) { throw "WSGM.Setup publish failed" }
Copy-Item -LiteralPath "$setupOut\WSGM.Setup.exe" -Destination "$root\publish\WSGM-Setup-$version.exe"

Get-ChildItem "$root\publish\WSGM-Setup-*.exe" |
    Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
