<#[
.SYNOPSIS
    Adds the issue-48 UWP launch supervisor spike to Steam as a non-Steam shortcut.

.DESCRIPTION
    Writes an entry into shortcuts.vdf for the published WsgmUwpSpike.exe, with the
    packaged title's AUMID in the launch options. Steam keeps shortcuts.vdf in memory
    and rewrites it on exit, so Steam has to be closed while this runs.

    The file is binary VDF: 0x00 opens a nested map, 0x01 is a NUL-terminated string,
    0x02 is a little-endian int32, and 0x08 closes a map. The existing file is parsed
    only far enough to find the next free index and to notice an entry that already
    points at the same executable.

.PARAMETER Aumid
    The packaged application user model id to launch, for example
    "11bitstudios.20925BA3921E0_gwy9gn5q9j1y6!App". Find one with Get-StartApps.

.PARAMETER Name
    The shortcut name shown in the Steam library.

.PARAMETER Exe
    The spike executable. Defaults to the published copy under publish\uwp-spike.

.PARAMETER ExtraArguments
    Additional spike flags, for example "--mode powershell --probe-rights".

.PARAMETER SteamPath
    Steam's install directory. Read from HKCU\Software\Valve\Steam when omitted.

.PARAMETER UserId
    The Steam userdata account id to write to. Required only when several exist.

.PARAMETER Force
    Add the entry even when one with the same executable is already present.

.PARAMETER DryRun
    Parse the existing file and report what would be written without changing anything.
    Safe to run while Steam is open, because nothing is written.
]#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Aumid,
    [string]$Name = "Moonlighter (WSGM UWP spike)",
    [string]$Exe,
    [string]$ExtraArguments = "--probe-rights --hide-console",
    [string]$SteamPath,
    [string]$UserId,
    [switch]$Force,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $Exe) {
    $Exe = Join-Path $root "publish\uwp-spike\WsgmUwpSpike.exe"
}

$Exe = [IO.Path]::GetFullPath($Exe)
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
    throw "The spike executable is missing: $Exe. Publish it first with " +
        "dotnet publish tools\UwpLaunchSpike\WSGM.UwpLaunchSpike.csproj -c Release -o publish\uwp-spike"
}

if (-not $DryRun -and (Get-Process -Name steam -ErrorAction SilentlyContinue)) {
    throw "Steam is running. Close Steam completely before writing shortcuts.vdf, or it will " +
        "overwrite the new entry from memory when it exits."
}

if (-not $SteamPath) {
    $SteamPath = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
}
if (-not $SteamPath -or -not (Test-Path -LiteralPath $SteamPath)) {
    throw "Could not locate the Steam installation. Pass -SteamPath."
}

$userdata = Join-Path $SteamPath "userdata"
$accounts = @(Get-ChildItem -LiteralPath $userdata -Directory | Where-Object { $_.Name -match '^\d+$' })
if ($UserId) {
    $accounts = @($accounts | Where-Object { $_.Name -eq $UserId })
}
if ($accounts.Count -eq 0) {
    throw "No Steam user account directory found under $userdata."
}
if ($accounts.Count -gt 1) {
    throw ("Several Steam accounts exist ({0}). Pass -UserId to pick one." -f ($accounts.Name -join ", "))
}

$configDirectory = Join-Path $accounts[0].FullName "config"
New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null
$shortcuts = Join-Path $configDirectory "shortcuts.vdf"

# Steam stores the Target quoted, exactly as its own UI writes it, and reads it back verbatim.
$quotedExe = '"' + $Exe + '"'
$startDirectory = '"' + (Split-Path -Parent $Exe) + '"'
$launchOptions = ('--aumid "{0}" {1}' -f $Aumid, $ExtraArguments).Trim()

function Read-CString([byte[]]$Bytes, [ref]$Position) {
    $start = $Position.Value
    while ($Position.Value -lt $Bytes.Length -and $Bytes[$Position.Value] -ne 0) { $Position.Value++ }
    $text = [Text.Encoding]::UTF8.GetString($Bytes, $start, $Position.Value - $start)
    $Position.Value++
    return $text
}

# Returns the index keys already present, every Exe value seen, and the offset of the
# 0x08 that closes the "shortcuts" map, which is where a new entry gets spliced in.
function Read-Shortcuts([byte[]]$Bytes) {
    $position = 0
    if ($Bytes.Length -lt 12 -or $Bytes[0] -ne 0) {
        throw "shortcuts.vdf does not start with a map header; refusing to rewrite it."
    }

    $position = 1
    $rootKey = Read-CString $Bytes ([ref]$position)
    if ($rootKey -ne "shortcuts") {
        throw "shortcuts.vdf has an unexpected root key '$rootKey'; refusing to rewrite it."
    }

    $depth = 1
    $indices = [Collections.Generic.List[string]]::new()
    $executables = [Collections.Generic.List[string]]::new()
    $closeOffset = -1

    while ($position -lt $Bytes.Length) {
        $type = $Bytes[$position]
        $typeOffset = $position
        $position++

        if ($type -eq 0x08) {
            $depth--
            if ($depth -eq 0) { $closeOffset = $typeOffset; break }
            continue
        }

        $key = Read-CString $Bytes ([ref]$position)
        switch ($type) {
            0x00 {
                if ($depth -eq 1) { $indices.Add($key) }
                $depth++
            }
            0x01 {
                $value = Read-CString $Bytes ([ref]$position)
                if ($depth -eq 2 -and $key -ieq "Exe") { $executables.Add($value) }
            }
            0x02 { $position += 4 }
            default { throw ("shortcuts.vdf contains an unsupported field type 0x{0:X2} at offset {1}." -f $type, $typeOffset) }
        }
    }

    if ($closeOffset -lt 0) {
        throw "shortcuts.vdf is truncated: the shortcuts map is never closed."
    }

    return [pscustomobject]@{
        Indices     = $indices
        Executables = $executables
        CloseOffset = $closeOffset
    }
}

function Get-Crc32([byte[]]$Bytes) {
    $polynomial = [uint32]3988292384   # 0xEDB88320, written in decimal so it stays unsigned.
    $table = New-Object uint32[] 256
    for ($i = 0; $i -lt 256; $i++) {
        $value = [uint32]$i
        for ($bit = 0; $bit -lt 8; $bit++) {
            if ($value -band 1) { $value = [uint32]($polynomial -bxor ($value -shr 1)) }
            else { $value = [uint32]($value -shr 1) }
        }
        $table[$i] = $value
    }

    # 4294967295 rather than 0xFFFFFFFF: PowerShell reads that hex literal as int -1.
    $crc = [uint32]4294967295
    foreach ($byte in $Bytes) {
        $crc = [uint32]($table[($crc -bxor $byte) -band 0xFF] -bxor ($crc -shr 8))
    }

    return [uint32]($crc -bxor [uint32]4294967295)
}

$builder = [Collections.Generic.List[byte]]::new()
function Add-Bytes([byte[]]$Bytes) { $script:builder.AddRange($Bytes) }
function Add-CString([string]$Text) {
    Add-Bytes ([Text.Encoding]::UTF8.GetBytes($Text))
    Add-Bytes ([byte[]]@(0))
}
function Add-StringField([string]$Key, [string]$Value) {
    Add-Bytes ([byte[]]@(0x01)); Add-CString $Key; Add-CString $Value
}
function Add-IntField([string]$Key, [int]$Value) {
    Add-Bytes ([byte[]]@(0x02)); Add-CString $Key; Add-Bytes ([BitConverter]::GetBytes($Value))
}

if (Test-Path -LiteralPath $shortcuts) {
    $existing = [IO.File]::ReadAllBytes($shortcuts)
} else {
    $existing = $null
}

if ($existing -and $existing.Length -gt 0) {
    $parsed = Read-Shortcuts $existing
    $indices = $parsed.Indices
    $closeOffset = $parsed.CloseOffset
    if (-not $Force -and ($parsed.Executables | Where-Object { $_ -ieq $quotedExe -or $_ -ieq $Exe })) {
        Write-Host "A shortcut for $Exe is already present; nothing was changed. Use -Force to add another."
        return
    }
} else {
    $header = [Collections.Generic.List[byte]]::new()
    $header.Add(0x00)
    $header.AddRange([Text.Encoding]::UTF8.GetBytes("shortcuts"))
    $header.Add(0x00)
    $existing = $header.ToArray()
    $indices = [Collections.Generic.List[string]]::new()
    $closeOffset = $existing.Length
}

$next = 0
foreach ($index in $indices) {
    $parsedIndex = 0
    if ([int]::TryParse($index, [ref]$parsedIndex) -and $parsedIndex -ge $next) { $next = $parsedIndex + 1 }
}

# A stable shortcut id: CRC32 over Target plus name with the high bit set, which is the
# shape Steam requires. It is deliberately not a reproduction of Steam's own derivation —
# the current client returned a different value for the same target and name — but Steam
# reads whatever id the file carries, so a stable unique one is enough and keeps grid
# artwork attached across rewrites. The value always has bit 31 set, so it reaches the
# int32 field through its bytes rather than through a checked cast.
$crc = Get-Crc32 ([Text.Encoding]::UTF8.GetBytes($quotedExe + $Name))
$appIdUnsigned = [uint32]($crc -bor [uint32]2147483648)
$appId = [BitConverter]::ToInt32([BitConverter]::GetBytes($appIdUnsigned), 0)

Add-Bytes ([byte[]]@(0x00))
Add-CString ([string]$next)
Add-IntField "appid" $appId
Add-StringField "AppName" $Name
Add-StringField "Exe" $quotedExe
Add-StringField "StartDir" $startDirectory
Add-StringField "icon" ""
Add-StringField "ShortcutPath" ""
Add-StringField "LaunchOptions" $launchOptions
Add-IntField "IsHidden" 0
Add-IntField "AllowDesktopConfig" 1
Add-IntField "AllowOverlay" 1
Add-IntField "OpenVR" 0
Add-IntField "Devkit" 0
Add-StringField "DevkitGameID" ""
Add-IntField "DevkitOverrideAppID" 0
Add-IntField "LastPlayTime" 0
Add-StringField "FlatpakAppID" ""
Add-Bytes ([byte[]]@(0x00)); Add-CString "tags"; Add-Bytes ([byte[]]@(0x08))
Add-Bytes ([byte[]]@(0x08))

$prefix = New-Object byte[] $closeOffset
[Array]::Copy($existing, 0, $prefix, 0, $closeOffset)

$output = [Collections.Generic.List[byte]]::new()
$output.AddRange($prefix)
$output.AddRange($builder)
$output.AddRange([byte[]]@(0x08, 0x08))

if ($DryRun) {
    Write-Host "Dry run; $shortcuts was not touched."
    Write-Host ("  Existing entries: {0}" -f ($(if ($indices.Count -eq 0) { "none" } else { $indices -join ", " })))
    Write-Host ("  Would write {0} bytes (currently {1})." -f $output.Count, $existing.Length)
} else {
    if (Test-Path -LiteralPath $shortcuts) {
        $backup = "$shortcuts.bak-" + (Get-Date -Format "yyyyMMdd-HHmmss")
        Copy-Item -LiteralPath $shortcuts -Destination $backup
        Write-Host "Backed up the previous file to $backup"
    }

    [IO.File]::WriteAllBytes($shortcuts, $output.ToArray())
}

Write-Host "Shortcut index $next for $shortcuts"
Write-Host "  Name          : $Name"
Write-Host "  Target        : $quotedExe"
Write-Host "  Start dir     : $startDirectory"
Write-Host "  Launch options: $launchOptions"
Write-Host "  Shortcut appid: $appId"
if (-not $DryRun) {
    Write-Host "Start Steam and the entry appears in the library."
}
