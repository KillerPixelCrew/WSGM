# Shared release build helpers. Callers own their output directories.

function Get-PinnedAsset {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$Url,
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Sha256,
        [string]$SignerThumbprint = "",
        [string]$SignerSubjectPattern = ""
    )

    function Assert-PinnedFile([string]$Candidate) {
        $hash = (Get-FileHash -LiteralPath $Candidate -Algorithm SHA256).Hash
        if ($hash -ine $Sha256) { throw "Digest mismatch for $Candidate; expected $Sha256, got $hash." }
        if ($SignerThumbprint -or $SignerSubjectPattern) {
            $signature = Get-AuthenticodeSignature -LiteralPath $Candidate
            if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
                throw "Authenticode validation failed for ${Candidate}: $($signature.Status)."
            }
            if ($SignerThumbprint -and $signature.SignerCertificate.Thumbprint -ine $SignerThumbprint) {
                throw "Signer mismatch for $Candidate."
            }
            if ($SignerSubjectPattern -and $signature.SignerCertificate.Subject -notmatch $SignerSubjectPattern) {
                throw "Signer subject mismatch for $Candidate."
            }
        }
    }

    $destination = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ieq $Sha256) {
            Assert-PinnedFile $destination
            return
        }
    }

    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    $partial = "$destination.partial"
    $previousProgress = $ProgressPreference
    try {
        $ProgressPreference = 'SilentlyContinue'
        for ($attempt = 1; ; $attempt++) {
            try {
                Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing
                break
            }
            catch {
                Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
                if ($attempt -ge 3) { throw }
                Start-Sleep -Seconds (2 * $attempt)
            }
        }
        Assert-PinnedFile $partial
        Move-Item -LiteralPath $partial -Destination $destination -Force
    }
    finally {
        $ProgressPreference = $previousProgress
        Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    }
}

function Get-DllExports {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string]$Path)

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw "Visual Studio locator not found: $vswhere" }
    $visualStudio = & $vswhere -latest -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $visualStudio) { throw 'Visual Studio C++ build tools not found.' }
    $installation = ([string]$visualStudio).Trim()
    $devCmd = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path -LiteralPath $devCmd)) { throw "Developer command script not found: $devCmd" }
    $dll = [IO.Path]::GetFullPath($Path)
    $command = "call `"$devCmd`" -no_logo -arch=x64 -host_arch=x64 >nul && dumpbin.exe /nologo /exports `"$dll`""
    $text = (& $env:ComSpec /d /s /c $command) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw "DLL export inspection failed: $dll" }
    $rows = @([regex]::Matches($text, '(?im)^\s*(\d+)\s+(?:[0-9A-F]+\s+)?[0-9A-F]+\s+(\S+)') |
        ForEach-Object { [pscustomobject]@{ Ordinal = [int]$_.Groups[1].Value; Name = $_.Groups[2].Value } })
    if ($rows.Count -eq 0) { throw "No DLL exports found: $dll" }
    return $rows
}

function Copy-RuntimeNotices {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$AssetsPath,
        [Parameter(Mandatory)] [string]$RuntimeIdentifier,
        [Parameter(Mandatory)] [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Restore assets are missing: $assetsPath"
    }

    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -Depth 100
    $runtimePackName = "Microsoft.NETCore.App.Runtime.$RuntimeIdentifier"
    $frameworks = @($assets.project.frameworks.psobject.Properties | ForEach-Object { $_.Value })
    $runtimeDependencies = @(
        $frameworks |
            ForEach-Object { $_.downloadDependencies } |
            Where-Object { [string]$_.name -ieq $runtimePackName }
    )
    if ($runtimeDependencies.Count -ne 1) {
        throw "Restore must resolve exactly one $runtimePackName pack."
    }

    # An exact pin, not a range: the notice must describe one specific redistributed runtime.
    $versionRange = ([string]$runtimeDependencies[0].version -replace '^\[|\]$', '')
    $bounds = @($versionRange.Split(',') | ForEach-Object { $_.Trim() })
    if ($bounds.Count -ne 2 -or $bounds[0] -cne $bounds[1] -or [string]::IsNullOrWhiteSpace($bounds[0])) {
        throw "Runtime pack version is not exact: $($runtimeDependencies[0].version)"
    }

    $runtimePack = $null
    foreach ($packageFolder in $assets.packageFolders.psobject.Properties.Name) {
        $candidate = Join-Path (Join-Path $packageFolder ($runtimePackName.ToLowerInvariant())) $bounds[0]
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $runtimePack = $candidate
            break
        }
    }
    if ($null -eq $runtimePack) {
        throw "Resolved runtime pack was not found in the restored package folders."
    }

    $copies = @(
        @{ Source = Join-Path $runtimePack "LICENSE.TXT"; Destination = "DotNetRuntime-LICENSE.txt" },
        @{ Source = Join-Path $runtimePack "THIRD-PARTY-NOTICES.TXT"; Destination = "DotNetRuntime-THIRD-PARTY-NOTICES.txt" }
    )
    foreach ($copy in $copies) {
        if (-not (Test-Path -LiteralPath $copy.Source -PathType Leaf)) {
            throw "Required licence or notice file is missing: $($copy.Source)"
        }
        $item = Get-Item -LiteralPath $copy.Source
        if ($item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Licence and notice files may not be copied through a link or reparse point: $($copy.Source)"
        }
        Copy-Item -LiteralPath $copy.Source -Destination (Join-Path $Destination $copy.Destination) -Force
    }
}
