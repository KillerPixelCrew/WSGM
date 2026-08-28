[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

$components = @(
    @{
        Id = 'HIDMaestro'
        File = 'HIDMaestro-v1.7.0.zip'
        Uri = 'https://github.com/hifihedgehog/HIDMaestro/releases/download/v1.7.0/HIDMaestro-v1.7.0.zip'
        Sha256 = 'A146AB8A46D2E9CE1FB2EA269FF231830607876F6F4DB7BB13CE891EF33DEECE'
        Thumbprint = $null
    },
    @{
        Id = 'usbip-win2'
        File = 'USBip-0.9.7.7-x64.exe'
        Uri = 'https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.7/USBip-0.9.7.7-x64.exe'
        Sha256 = '51620FA5F9F8BE5932BC9D786DEEE557CE06D5407A99CAB490DCFAC71F185FEA'
        Thumbprint = '9AC56B6C76141395D74FFF6652818376E80B9C95'
    },
    @{
        Id = 'HidHide'
        File = 'HidHide_1.5.230_x64.exe'
        Uri = 'https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe'
        Sha256 = 'F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6'
        Thumbprint = '1F431092EC96A80B41AB5317F53AC02EA6F9B89B'
    }
)

foreach ($component in $components) {
    $componentDirectory = Join-Path $destinationRoot $component.Id
    New-Item -ItemType Directory -Path $componentDirectory -Force | Out-Null
    $assetPath = Join-Path $componentDirectory $component.File
    Invoke-WebRequest -Uri $component.Uri -OutFile $assetPath

    $actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -cne $component.Sha256) {
        throw "Hash mismatch for $($component.File): expected $($component.Sha256), got $actualHash."
    }

    if ($null -ne $component.Thumbprint) {
        $signature = Get-AuthenticodeSignature -LiteralPath $assetPath
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Authenticode validation failed for $($component.File): $($signature.Status)."
        }

        $actualThumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
        if ($actualThumbprint -cne $component.Thumbprint) {
            throw "Signer mismatch for $($component.File): got $actualThumbprint."
        }
    }
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'wsgm-controller-acquire-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $archive = Join-Path $destinationRoot 'HIDMaestro\HIDMaestro-v1.7.0.zip'
    Expand-Archive -LiteralPath $archive -DestinationPath $temporaryRoot
    $sdkSource = Join-Path $temporaryRoot 'HIDMaestro.Core.dll'
    $sdkHash = (Get-FileHash -LiteralPath $sdkSource -Algorithm SHA256).Hash.ToUpperInvariant()
    $expectedSdkHash = 'BD42A99BCB260435CE25796C54A4B792F8A2CED6AB78659C0CF926011663938E'
    if ($sdkHash -cne $expectedSdkHash) {
        throw "HIDMaestro.Core.dll hash mismatch: expected $expectedSdkHash, got $sdkHash."
    }

    $sdkDirectory = Join-Path $destinationRoot 'HIDMaestro\1.7.0'
    New-Item -ItemType Directory -Path $sdkDirectory -Force | Out-Null
    foreach ($file in @('HIDMaestro.Core.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $temporaryRoot $file) -Destination $sdkDirectory -Force
    }
}
finally {
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase)
        -and (Split-Path -Leaf $resolvedTemporaryRoot).StartsWith(
            'wsgm-controller-acquire-',
            [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}

Write-Host "Pinned controller artifacts acquired and verified under $destinationRoot."
Write-Host 'Nothing was executed or installed.'
