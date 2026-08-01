# WSGM release build: NativeAOT publish + Inno Setup installer.
# Output: publish\WSGM-Setup-<version>.exe (the one-file installer)
#         publish\WSGM.exe + *.dll        (portable files)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# NativeAOT needs the VS linker toolchain; ILCompiler locates it via vswhere.
$env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

# The csproj <Version> is the single source of truth; the installer gets it via /D.
$csproj = Get-Content "$root\src\WSGM\WSGM.csproj" -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw "No <Version> found in WSGM.csproj" }
$version = $Matches[1]

Write-Host "== Publishing WSGM $version (NativeAOT) ==" -ForegroundColor Cyan
# Clean first: dotnet publish overlays onto the previous output, so a DLL removed by
# a dependency bump (or an old setup exe) would otherwise leak into the release.
Remove-Item -Recurse -Force "$root\publish" -ErrorAction SilentlyContinue
dotnet publish "$root\src\WSGM\WSGM.csproj" -c Release -r win-x64 -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

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
