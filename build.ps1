# WSGM release build: NativeAOT publish + Inno Setup installer.
# Output: publish\WSGM-Setup-<version>.exe (the one-file installer — the only
# shipped artifact; the logon service requires a real install)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# NativeAOT needs the VS linker toolchain; ILCompiler locates it via vswhere.
$env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

# The csproj <Version> is the single source of truth; the installer gets it via /D.
$csproj = Get-Content "$root\src\WSGM\WSGM.csproj" -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "No <Version> found in WSGM.csproj" }
$version = $Matches[1]

# These checks use only checked-in metadata and Node built-ins. Run them before
# the native toolchains so a stale asset hash or broken component graph fails
# in seconds rather than after a full release publish.
Write-Host "== Validating release inputs ==" -ForegroundColor Cyan
npm run steam-assets:verify
if ($LASTEXITCODE -ne 0) { throw "Steam UI asset drift check failed" }
& "$root\eng\assert-build-graph.ps1"

# The Steam Input gate is built from the source in native\SteamInput on every
# release build, so a shipped installer can never carry a gate older than the
# code beside it. This must precede the publish, which copies the staged output.
Write-Host "== Building Steam Input Lease (Rust) ==" -ForegroundColor Cyan
# -Validate for the export check: build.rs now drives exports from one authoritative
# .def, and the dumpbin ordinal comparison is the ONLY thing that catches link.exe
# putting an unrelated symbol at XInput's ordinal 104/109 - the stack-corruption case
# that .def exists to prevent. Without this the shipped DLL is the one artifact never
# export-checked, since eng\verify.ps1 only validates a separately built copy.
& "$root\eng\build-steam-input-lease.ps1" -Validate

# Same rule as the lease: built from source on every release so the shipped
# helper can never be older than the code beside it, and staged before the
# publish that copies it.
Write-Host "== Building radio helper (Rust) ==" -ForegroundColor Cyan
& "$root\eng\build-radio.ps1"

Write-Host "== Publishing WSGM $version (NativeAOT) ==" -ForegroundColor Cyan
# Clean first: dotnet publish overlays onto the previous output, so a DLL removed by
# a dependency bump (or an old setup exe) would otherwise leak into the release.
# Test-Path covers the only tolerable failure (no previous output); a clean that
# fails for any other reason must stop the build, not leak a stale tree.
if (Test-Path "$root\publish") { Remove-Item -Recurse -Force "$root\publish" }
$appPublish = "$root\publish\App"
New-Item -ItemType Directory -Path $appPublish | Out-Null

# One RID-aware restore feeds every --no-restore publish below. ProjectReference
# edges establish dependency order; the static graph assertion above catches a
# new project that was omitted from the solution or crosses the AOT/JIT boundary.
dotnet restore "$root\WSGM.slnx" --runtime win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

dotnet publish "$root\src\WSGM\WSGM.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The user-facing Steam launch wrapper. Steam inherits WSGM's elevation, so this
# hands the real command to a medium-integrity scheduled-task child and/or holds a
# Steam Input block lease for the game's lifetime. Publish it beside WSGM so both
# portable and installed layouts use the same stable command path.
dotnet publish "$root\src\WSGM.Launch\WSGM.Launch.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore "/p:Version=$version"
if ($LASTEXITCODE -ne 0) { throw "WSGM.Launch publish failed" }

# The SYSTEM logon service that launches WSGM's boot cover at sign-in. Published
# beside the rest; the installer ships it to Program Files (never user-writable).
dotnet publish "$root\src\WSGM.LogonService\WSGM.LogonService.csproj" -c Release -r win-x64 `
    -o $appPublish --no-restore "/p:Version=$version"
if ($LASTEXITCODE -ne 0) { throw "WSGM.LogonService publish failed" }

# Core Audio is a COM API. WSGM's NativeAOT executable intentionally has managed
# COM interop disabled, so compile the tiny ABI-only helper that owns those calls
# and place it alongside WSGM.exe for LibraryImport to load at runtime.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio locator not found: $vswhere" }
$visualStudio = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw "Visual Studio C++ build tools not found" }
$devCmd = Join-Path $visualStudio.Trim() "Common7\Tools\VsDevCmd.bat"
if (-not (Test-Path $devCmd)) { throw "Visual Studio developer command script not found: $devCmd" }

$nativeSource = "$root\native\VolumeControl\VolumeControl.cpp"
$nativeOutput = "$appPublish\WSGM.VolumeControl.dll"
$nativeTemp = Join-Path ([System.IO.Path]::GetTempPath()) "WSGM-VolumeControl-$PID"
$nativeTempOutput = Join-Path $nativeTemp "WSGM.VolumeControl.dll"
New-Item -ItemType Directory -Path $nativeTemp | Out-Null
try {
    # Compile in a disposable directory: link.exe also emits .lib/.exp files,
    # neither of which belongs in the portable publish layout.
    $compile = "call `"$devCmd`" -no_logo -arch=x64 -host_arch=x64 >nul && pushd `"$nativeTemp`" && cl.exe /nologo /std:c++17 /O2 /LD `"$nativeSource`" /link ole32.lib winmm.lib /OUT:`"$nativeTempOutput`" /INCREMENTAL:NO"
    & $env:ComSpec /d /s /c $compile
    if ($LASTEXITCODE -ne 0) { throw "VolumeControl native helper build failed" }
    Copy-Item $nativeTempOutput $nativeOutput
}
finally {
    Remove-Item -Recurse -Force $nativeTemp -ErrorAction SilentlyContinue
}
if (-not (Test-Path $nativeOutput)) { throw "VolumeControl native helper was not produced" }
if (-not (Test-Path "$appPublish\WSGM.Radio.dll")) { throw "Radio helper was not published" }
if (-not (Test-Path "$appPublish\WSGM.Launch.exe")) { throw "Launch wrapper was not produced" }
if (-not (Test-Path "$appPublish\WSGM.LogonService.exe")) { throw "Logon service was not produced" }

# The AOT/JIT split only holds if nothing from the device platform is staged beside WSGM.exe.
# A wrong ProjectReference is caught by DeviceBoundaryTests; a binary that arrives by copy has no
# compile-time symptom at all, so it is checked here against the finished publish layout.
& "$root\eng\check-aot-isolation.ps1" -OutputDirectory $appPublish

Write-Host "== Publishing isolated device components ==" -ForegroundColor Cyan
& "$root\eng\stage-device-components.ps1" `
    -OutputRoot "$root\publish" `
    -Configuration Release `
    -RuntimeIdentifier win-x64 `
    -Version $version `
    -NoRestore
& "$root\eng\assert-component-staging.ps1" -OutputRoot "$root\publish"

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }

Write-Host "== Compiling installer ==" -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" "$root\installer\WSGM.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

& "$root\eng\write-artifact-manifests.ps1" `
    -OutputRoot "$root\publish" `
    -Version $version `
    -Configuration Release `
    -RuntimeIdentifier win-x64

Get-ChildItem "$root\publish\WSGM-Setup-*.exe" |
    Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
