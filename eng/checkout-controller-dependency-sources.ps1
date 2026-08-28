[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

$sources = @(
    @{
        Id = 'HIDMaestro'
        Repository = 'https://github.com/hifihedgehog/HIDMaestro.git'
        Commit = '46054b862830fcec7bc98d72ccb7c4f0c0179fb1'
    },
    @{
        Id = 'usbip-win2'
        Repository = 'https://github.com/vadimgrn/usbip-win2.git'
        Commit = '7c219953101cc5d0ec9a0bcb3eb87259cf72bedd'
    },
    @{
        Id = 'HidHide'
        Repository = 'https://github.com/nefarius/HidHide.git'
        Commit = '722d997ce75db58f5aa36e40ca920f99022c020a'
    }
)

foreach ($source in $sources) {
    $sourceDirectory = Join-Path $destinationRoot $source.Id
    if (Test-Path -LiteralPath $sourceDirectory) {
        throw "Refusing to replace existing source directory: $sourceDirectory"
    }

    & git clone --filter=blob:none --no-checkout $source.Repository $sourceDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "git clone failed for $($source.Id)."
    }

    & git -C $sourceDirectory checkout --detach $source.Commit
    if ($LASTEXITCODE -ne 0) {
        throw "git checkout failed for $($source.Id) at $($source.Commit)."
    }

    $actualCommit = (& git -C $sourceDirectory rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -cne $source.Commit) {
        throw "Commit verification failed for $($source.Id): got $actualCommit."
    }
}

Write-Host "Exact reviewed sources checked out under $destinationRoot."
Write-Host 'No build, driver operation, executable launch, or install was performed.'
