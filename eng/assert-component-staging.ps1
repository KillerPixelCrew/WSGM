<#
.SYNOPSIS
    Fails when isolated release staging contains a boundary or package-safety violation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outputFull = [IO.Path]::GetFullPath($OutputRoot)

function Require-File([string]$RelativePath) {
    $path = Join-Path $outputFull $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required staged artifact is missing: $RelativePath"
    }
}

function Assert-NoLinks([string]$Directory) {
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force -Recurse) {
        if ($entry.LinkType -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Release staging may not contain links or reparse points: $($entry.FullName)"
        }
    }
}

Require-File "setup-distribution.json"
$distribution = Get-Content -LiteralPath (Join-Path $outputFull "setup-distribution.json") -Raw | ConvertFrom-Json
if ($distribution.schemaVersion -ne 1 -or $distribution.prerequisites -cnotin @("bundled", "download")) {
    throw "Unsupported setup distribution marker."
}
$bundled = $distribution.prerequisites -ceq "bundled"

Require-File "App\WSGM.exe"
Require-File "App\WSGM.ShellAnchor.exe"
Require-File "App\WSGM.Launch.exe"
Require-File "App\WSGM.LogonService.exe"
Require-File "App\LICENSE.txt"
Require-File "App\WebView2Loader.dll"
Require-File "App\Microsoft.Web.WebView2.Core.dll"
foreach ($browserFile in @("WSGM.BrowserHost.exe", "WSGM.BrowserHost.dll", "WSGM.BrowserHost.deps.json",
        "WSGM.BrowserHost.runtimeconfig.json", "Microsoft.Web.WebView2.Core.dll", "WebView2Loader.dll",
        "System.Windows.Forms.dll", "coreclr.dll", "hostfxr.dll")) {
    Require-File "App\Browser\$browserFile"
}
foreach ($screensaverFile in @("WSGM.ScreensaverHost.exe", "WSGM.ScreensaverHost.dll",
        "WSGM.ScreensaverHost.deps.json", "WSGM.ScreensaverHost.runtimeconfig.json",
        "Microsoft.Web.WebView2.Core.dll", "WebView2Loader.dll", "System.Windows.Forms.dll", "coreclr.dll")) {
    Require-File "App\Screensaver\$screensaverFile"
}
Require-File "App\WebView2-LICENSE.txt"
Require-File "App\Microsoft.Data.Sqlite-MIT.txt"
Require-File "App\SQLitePCLRaw-Apache-2.0.txt"
Require-File "App\DotNetRuntime-LICENSE.txt"
Require-File "App\DotNetRuntime-THIRD-PARTY-NOTICES.txt"
foreach ($family in @("Ayaneo", "Generic", "Gpd", "LegionGo", "MsiClaw", "OneXPlayer", "RogAlly", "SteamDeck", "Zotac")) {
    Require-File "App\LibHandheld-$family-THIRD_PARTY_NOTICES.md"
}
Require-File "Controller\libviiper.dll"
Require-File "Controller\libviiper.h"
Require-File "Controller\VIIPER-LICENSE.txt"
Require-File "Controller\VIIPER-NOTICE.md"
Require-File "Controller\Install-UsbipDriver.ps1"
Require-File "Controller\usbip-win2-BSD-2-Clause.txt"
Require-File "Controller\HidHide-MIT.txt"
$controllerAssets = @((Get-Content -LiteralPath (Join-Path $root "external\controller\controller-components.lock.json") -Raw |
    ConvertFrom-Json).components.asset)
Require-File "bundle.json"

$runtimeLock = Get-Content -LiteralPath (Join-Path $root "eng\rtss-runtime.lock.json") -Raw | ConvertFrom-Json
if ($runtimeLock.asset -cne "rtss.zip" -or $runtimeLock.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
    [IO.Path]::GetFileName([string]$runtimeLock.setupEntry) -cne $runtimeLock.setupEntry) {
    throw "RTSS runtime pin must name rtss.zip, a SHA-256 digest and a root installer entry."
}
if ($bundled) {
    Require-File "MediaRuntime\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
    foreach ($asset in $controllerAssets) { Require-File "Controller\$asset" }
    Require-File "PerformanceRuntime\rtss.zip"
    $rtssArchive = Join-Path $outputFull "PerformanceRuntime\rtss.zip"
    if ((Get-FileHash -LiteralPath $rtssArchive -Algorithm SHA256).Hash -ine $runtimeLock.sha256) {
        throw "Staged RTSS archive does not match its reviewed SHA-256."
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($rtssArchive)
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $runtimeLock.setupEntry })
        if ($entries.Count -ne 1 -or $entries[0].Length -eq 0) {
            throw "Staged RTSS archive must contain exactly one nonempty $($runtimeLock.setupEntry)."
        }
    }
    finally {
        $archive.Dispose()
    }
}
else {
    foreach ($path in (@("MediaRuntime", "PerformanceRuntime") + @($controllerAssets | ForEach-Object { "Controller\$_" }))) {
        if (Test-Path -LiteralPath (Join-Path $outputFull $path)) {
            throw "Online setup must download its prerequisites instead of bundling $path."
        }
    }
    # These pins are embedded in Setup, rather than installed with WSGM. Confirm every exact
    # input and resource name used by the download path before assembling either executable.
    [xml]$setupProject = Get-Content -LiteralPath (Join-Path $root "src\WSGM.Setup\WSGM.Setup.csproj") -Raw
    $requiredPins = @{
        "WSGM.ControllerComponentsPin" = "..\..\external\controller\controller-components.lock.json"
        "WSGM.WebView2RuntimePin" = "..\..\eng\webview2-runtime.lock.json"
        "WSGM.RtssRuntimePin" = "..\..\eng\rtss-runtime.lock.json"
    }
    foreach ($name in $requiredPins.Keys) {
        $resources = @($setupProject.SelectNodes("//EmbeddedResource") | Where-Object { $_.GetAttribute("LogicalName") -ceq $name })
        if ($resources.Count -ne 1 -or $resources[0].GetAttribute("Include") -cne $requiredPins[$name] -or
            -not (Test-Path -LiteralPath (Join-Path "$root\src\WSGM.Setup" $requiredPins[$name]) -PathType Leaf)) {
            throw "Online setup requires its pinned download resource: $name."
        }
    }
}
Assert-NoLinks $outputFull

$packagesDirectory = Join-Path $outputFull "Packages"
if (@(Get-ChildItem -LiteralPath $packagesDirectory -Directory).Count -ne 0) {
    throw "Plugin packages are staged as .wsgmpkg files, never as unpacked directories."
}
$packageFiles = @(
    Get-ChildItem -LiteralPath $packagesDirectory -File -Filter "*.wsgmpkg" |
    Sort-Object FullName
)
if ($packageFiles.Count -eq 0) {
    throw "No plugin package was staged."
}

# bundle.json must describe exactly the staged files, byte for byte.
$bundle = Get-Content -LiteralPath (Join-Path $outputFull "bundle.json") -Raw | ConvertFrom-Json -Depth 32
$listed = @($bundle.plugins)
if ($listed.Count -ne $packageFiles.Count) {
    throw "bundle.json lists $($listed.Count) plugin(s) but $($packageFiles.Count) package file(s) are staged."
}
foreach ($entry in $listed) {
    $staged = Join-Path $packagesDirectory ([string]$entry.file)
    if (-not (Test-Path -LiteralPath $staged -PathType Leaf)) {
        throw "bundle.json names $($entry.file), which is not staged."
    }
    if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne ([string]$entry.sha256).ToUpperInvariant()) {
        throw "bundle.json's hash for $($entry.file) does not match the staged file."
    }
}

$forbiddenExtensions = @(
    ".pdb", ".cs", ".csx", ".ps1", ".psm1", ".pfx", ".p12", ".snk", ".key",
    ".pem", ".pvk", ".jks", ".keystore", ".etl", ".evtx", ".pcap", ".pcapng",
    ".dmp", ".dump", ".wsgmcap", ".zip", ".7z"
)
$textExtensions = @(".json", ".xml", ".config", ".txt", ".md", ".js", ".css", ".svg", ".h")
# Separators are [\\/]+ so a JSON-escaped path (C:\\Users\\...) still matches. Besides the usual
# developer roots, the pattern names this checkout's own root and the CI workspace shape (D:\a\),
# because a generic list missed a root such as E:\SourceCode\.
$separator = '[\\/]+'
$rootPattern = (($root.TrimEnd('\', '/') -split '[\\/]') | ForEach-Object { [regex]::Escape($_) }) -join $separator
$localPathPattern = "(?i)(?:$rootPattern|[A-Z]:$separator(?:Users|Coding|Repos?|Source\w*|Src|Dev|Projects?|Worktrees?|Git|a)$separator|\\\\\?\\[A-Z]:\\)"
$secretPattern = '(?i)(?:password|passwd|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[=:]\s*["'']?[^\s"'']{8,}'

# Text leak checks shared by every shipped tree.
function Assert-NoLeaks([IO.FileInfo]$File, [string]$Label) {
    if ($File.Extension -notin $textExtensions -or $File.Length -gt 4MB) {
        return
    }
    $text = Get-Content -LiteralPath $File.FullName -Raw
    if ($text -match $localPathPattern) {
        throw "$Label leaks a local developer path: $($File.FullName)"
    }
    if ($text -match $secretPattern) {
        throw "$Label contains a secret-shaped assignment: $($File.FullName)"
    }
}

# Material that must never reach a user, wherever it is shipped from.
function Assert-NoDeveloperMaterial([IO.FileInfo]$File, [string]$Label) {
    if ($File.Extension -in $forbiddenExtensions) {
        throw "$Label contains source/debug/capture/key material: $($File.FullName)"
    }
    if ($File.Name -in @(".env", "secrets.json", "appsettings.Development.json", "NuGet.Config")) {
        throw "$Label contains a credential-bearing developer file: $($File.FullName)"
    }
}

# The setup payload ships everything in App and Controller; build.ps1 fills them from an explicit
# allowlist, so nothing else should be there.
foreach ($directory in @("App", "Controller", "MediaRuntime", "PerformanceRuntime")) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputFull $directory) -PathType Container)) {
        continue
    }
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $outputFull $directory) -File -Recurse)) {
        $relative = [IO.Path]::GetRelativePath($outputFull, $file.FullName).Replace("\", "/")
        # Only the exact archive already checked above may bypass the source ZIP prohibition.
        $pinnedRtss = $bundled -and $relative -ceq "PerformanceRuntime/rtss.zip"
        if ($file.Extension -ne ".ps1" -and -not $pinnedRtss) {
            Assert-NoDeveloperMaterial $file "$directory payload"
        }
        Assert-NoLeaks $file "$directory payload"
    }
}

# The package ships as one file; check its contents through a throwaway expansion.
$expansionRoot = Join-Path ([IO.Path]::GetTempPath()) ("WSGM-PackageCheck-{0}" -f [Guid]::NewGuid().ToString("N"))
try {
foreach ($packageFile in $packageFiles) {
    $packageRoot = Get-Item -LiteralPath (New-Item -ItemType Directory -Path (Join-Path $expansionRoot $packageFile.BaseName)).FullName
    [IO.Compression.ZipFile]::ExtractToDirectory($packageFile.FullName, $packageRoot.FullName)
    foreach ($required in @("plugin.wsgm.json", "LICENSE.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageRoot.FullName $required) -PathType Leaf)) {
            throw "Plugin package $($packageFile.Name) is missing $required."
        }
    }

    $manifest = Get-Content -LiteralPath (Join-Path $packageRoot.FullName "plugin.wsgm.json") -Raw |
        ConvertFrom-Json -Depth 32
    if ($packageFile.Name -cne ("{0}-{1}.wsgmpkg" -f [string]$manifest.id, [string]$manifest.version)) {
        throw "Plugin package file name does not match its manifest identity and version: $($packageFile.Name)"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot.FullName ([string]$manifest.entryAssembly)) -PathType Leaf)) {
        throw "Plugin package $($packageFile.Name) is missing its entry assembly."
    }

    $files = @(Get-ChildItem -LiteralPath $packageRoot.FullName -File -Recurse | Sort-Object FullName)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($packageRoot.FullName, $file.FullName).Replace("\", "/")
        Assert-NoDeveloperMaterial $file "Plugin package"
        if ($relative -match '(?i)(?:^|/)(?:captures?|raw[-_]?evidence|fixtures?|recipes?)(?:/|$)') {
            throw "Plugin package contains a source-capture or evidence directory: $relative"
        }
        if ($file.Name -in @("WSGM.exe", "WSGM.Launch.exe", "WSGM.LogonService.exe",
            "wsgm-device.exe")) {
            throw "Plugin package contains an unrelated WSGM executable: $relative"
        }
        Assert-NoLeaks $file "Plugin package"
    }
}
}
finally {
    if (Test-Path -LiteralPath $expansionRoot) {
        Remove-Item -LiteralPath $expansionRoot -Recurse -Force
    }
}

Write-Host "Component isolation and package staging assertions passed."
