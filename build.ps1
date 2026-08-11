# WSGM release build: NativeAOT publish + Inno Setup installer.
# Output: publish\WSGM-Setup-<version>.exe (the one-file installer — the only
# shipped artifact; the logon service requires a real install)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# NativeAOT needs the VS linker toolchain; ILCompiler locates it via vswhere.
$env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

# The csproj <Version> is the single source of truth; the installer gets it via /D.
$csproj = Get-Content "$root\src\WSGM\WSGM.csproj" -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "No <Version> found in WSGM.csproj" }
$version = $Matches[1]

# The Steam Input gate is built from the source in native\SteamInput on every
# release build, so a shipped installer can never carry a gate older than the
# code beside it. This must precede the publish, which copies the staged output.
Write-Host "== Building Steam Input Lease (Rust) ==" -ForegroundColor Cyan
& "$root\eng\build-steam-input-lease.ps1"

# Same rule as the lease: built from source on every release so the shipped
# helper can never be older than the code beside it, and staged before the
# publish that copies it.
Write-Host "== Building radio helper (Rust) ==" -ForegroundColor Cyan
& "$root\eng\build-radio.ps1"

Write-Host "== Publishing WSGM $version (NativeAOT) ==" -ForegroundColor Cyan
# Clean first: dotnet publish overlays onto the previous output, so a DLL removed by
# a dependency bump (or an old setup exe) would otherwise leak into the release.
Remove-Item -Recurse -Force "$root\publish" -ErrorAction SilentlyContinue
dotnet publish "$root\src\WSGM\WSGM.csproj" -c Release -r win-x64 -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Steam inherits WSGM's elevation. This small wrapper is pasted into a game's
# launch options and hands the real command to a medium-integrity scheduled-task
# child. Publish it beside WSGM so both portable and installed layouts use the
# same stable command path.
dotnet publish "$root\src\WSGM.Deelevate\WSGM.Deelevate.csproj" -c Release -r win-x64 `
    -o "$root\publish" "/p:Version=$version"
if ($LASTEXITCODE -ne 0) { throw "WSGM.Deelevate publish failed" }

# Some games/mod tools need Windows Explorer running. This wrapper is pasted into
# a game's launch options and asks the running WSGM shell to drop to desktop mode
# (Explorer up) for the game's lifetime. Published beside WSGM like the others.
dotnet publish "$root\src\WSGM.Explorerfy\WSGM.Explorerfy.csproj" -c Release -r win-x64 `
    -o "$root\publish" "/p:Version=$version"
if ($LASTEXITCODE -ne 0) { throw "WSGM.Explorerfy publish failed" }

# The SYSTEM logon service that launches WSGM's boot cover at sign-in. Published
# beside the rest; the installer ships it to Program Files (never user-writable).
dotnet publish "$root\src\WSGM.LogonService\WSGM.LogonService.csproj" -c Release -r win-x64 `
    -o "$root\publish" "/p:Version=$version"
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
$nativeOutput = "$root\publish\WSGM.VolumeControl.dll"
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
if (-not (Test-Path "$root\publish\WSGM.Radio.dll")) { throw "Radio helper was not published" }
if (-not (Test-Path "$root\publish\WSGM.Deelevate.exe")) { throw "De-elevation helper was not produced" }
if (-not (Test-Path "$root\publish\WSGM.Explorerfy.exe")) { throw "Explorer-companion helper was not produced" }
if (-not (Test-Path "$root\publish\WSGM.LogonService.exe")) { throw "Logon service was not produced" }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }

Write-Host "== Compiling installer ==" -ForegroundColor Cyan
& $iscc "/DAppVersion=$version" "$root\installer\WSGM.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem "$root\publish\WSGM-Setup-*.exe" |
    Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
