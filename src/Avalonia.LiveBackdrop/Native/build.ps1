param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Install Visual Studio C++ build tools and the Windows SDK.' }
$developerCommand = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$environmentLines = & cmd.exe /d /s /c "call `"$developerCommand`" -arch=x64 -host_arch=x64 >nul && set"
if ($LASTEXITCODE -ne 0) { throw 'Visual Studio build environment initialization failed.' }
foreach ($line in $environmentLines) {
    if ($line -match '^([^=]+)=(.*)$') {
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
    }
}
Push-Location $outputPath
try {
    & cl.exe /nologo /std:c++17 /EHsc /O2 /MT /W4 /WX /wd4191 /LD "$PSScriptRoot/Backdrop.cpp" `
        /Fe:Avalonia.LiveBackdrop.Native.dll /Fo:Backdrop.obj `
        /link user32.lib comctl32.lib ole32.lib dwmapi.lib d3d11.lib dcomp.lib
    if ($LASTEXITCODE -ne 0) { throw 'LiveBackdrop native compilation failed.' }
} finally {
    Pop-Location
}
