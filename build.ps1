# OpenFSE release build: NativeAOT publish + Inno Setup installer.
# Output: publish\OpenFSE-Setup-<version>.exe (the one-file installer)
#         publish\OpenFSE.exe + *.dll        (portable files)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# NativeAOT needs the VS linker toolchain; ILCompiler locates it via vswhere.
$env:Path += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

Write-Host "== Publishing OpenFSE (NativeAOT) ==" -ForegroundColor Cyan
dotnet publish "$root\src\OpenFSE\OpenFSE.csproj" -c Release -r win-x64 -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }

Write-Host "== Compiling installer ==" -ForegroundColor Cyan
& $iscc "$root\installer\OpenFSE.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem "$root\publish\OpenFSE-Setup-*.exe" |
    Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
