# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
Creates a common plugin project with a harmless status/action example.
.DESCRIPTION
Creates a new directory only. The generated project references this checkout's common SDK, and its
manifest takes the SDK's API version and passes the SDK's manifest validation before anything is
written. No package is installed, enabled, or executed by this command.

-Category wsgm.gpu creates a graphics driver plugin instead: it takes -PciVendorId, the adapter
vendor it serves (8086 Intel, 10DE NVIDIA, 1002 AMD), declares one generic toggle and implements
ICapabilityPlugin without publishing anything yet. Built-in vendor engines use LibGPUDriverInteract
directly; this template remains for independent third-party capability publishers.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$Output,
    [string]$Category = 'wsgm.peripheral',
    [string]$PciVendorId = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Category -eq 'wsgm.device') { throw 'Use Device Lab scaffold for the Device specialization.' }
$gpu = $Category -eq 'wsgm.gpu'
if ($gpu -and [string]::IsNullOrWhiteSpace($PciVendorId)) { throw 'A graphics plugin needs -PciVendorId, for example 8086.' }
if (-not $gpu -and -not [string]::IsNullOrWhiteSpace($PciVendorId)) { throw '-PciVendorId applies only to -Category wsgm.gpu.' }
$target = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $target) { throw "Output already exists: $target" }
$parent = [IO.Directory]::GetParent($target)
if ($null -eq $parent -or -not $parent.Exists) { throw 'The output parent must already exist.' }
for ($ancestor = $parent; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Output cannot traverse a reparse point.' }
}
$manifestTool = Join-Path $PSScriptRoot 'plugin-manifest.cs'
$apiOutput = @(& dotnet run --file $manifestTool -- api-version 2>&1)
if ($LASTEXITCODE -ne 0) { throw "Reading the Plugin SDK API version failed:`n$($apiOutput -join [Environment]::NewLine)" }
$apiVersion = [int]"$($apiOutput[-1])"
$entryType = if ($gpu) { 'ExamplePlugin.Gpu.Plugin' } else { 'ExamplePlugin.Common.Plugin' }
$manifest = [ordered]@{
    id = $Id; name = $Id; version = '0.1.0'; category = $Category
    minimumApiVersion = $apiVersion; maximumApiVersion = $apiVersion
    entryAssembly = 'ExamplePlugin.dll'; entryType = $entryType
    dependencies = @(); permissions = @()
}
if ($gpu) {
    # Typed arrays so a single entry stays a JSON list.
    [object[]]$adapters = @([ordered]@{ pciVendorId = $PciVendorId.ToUpperInvariant() })
    [object[]]$roles = @('GenericToggle')
    $manifest.displayAdapters = $adapters
    $manifest.capabilities = $roles
}
$manifestJson = $manifest | ConvertTo-Json -Depth 8
# The identity is spliced into generated source below, so it must pass the SDK's rules first.
$manifestProbe = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllText($manifestProbe, $manifestJson)
    $validation = @(& dotnet run --file $manifestTool -- validate $manifestProbe 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "The Plugin SDK rejected the plugin identity:`n$($validation -join [Environment]::NewLine)" }
}
finally {
    Remove-Item -LiteralPath $manifestProbe -Force -ErrorAction SilentlyContinue
}
$sdk = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj'))
$sdkXml = [Security.SecurityElement]::Escape($sdk)
[void][IO.Directory]::CreateDirectory($target)
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <AssemblyName>ExamplePlugin</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$sdkXml" />
    <None Update="plugin.wsgm.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $target 'Plugin.csproj'), $project)
$templateKind = if ($gpu) { "GpuPlugin" } else { "CommonPlugin" }
$template = Get-Content -LiteralPath (Join-Path $PSScriptRoot "templates\$templateKind\Plugin.cs") -Raw
[IO.File]::WriteAllText((Join-Path $target 'Plugin.cs'), $template.Replace('__PLUGIN_ID__', $Id))
[IO.File]::WriteAllText((Join-Path $target 'plugin.wsgm.json'), $manifestJson)
Write-Output "Created $target. Build with dotnet build, then package with eng/package-plugin.ps1."
