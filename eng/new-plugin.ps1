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
ICapabilityPlugin without publishing anything yet. src/WSGM.Plugin.IntelGpu is the worked example.
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
$manifest = [ordered]@{
    id = $Id; name = $Id; version = '0.1.0'; category = $Category
    minimumApiVersion = $apiVersion; maximumApiVersion = $apiVersion
    entryAssembly = 'ExamplePlugin.dll'; entryType = 'ExamplePlugin.Plugin'
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
$source = @'
using WSGM.Plugin.Sdk;

namespace ExamplePlugin;

// No external resources are acquired by this constructor or example.
public sealed class Plugin : IPlugin, IPluginActions, IPluginUi
{
    public string Id => "__PLUGIN_ID__";
    private IPluginHost? _host;
    private long _sequence;
    private int _count;
    public IReadOnlyList<PluginAction> Actions => [new("increment", "Increment example counter", [])];
    public IReadOnlyList<PluginUiContribution> Contributions => [
        new("counter", "Example counter", "example", PluginUiKind.Status, StateKey: "count"),
        new("increment", "Increment", "example", PluginUiKind.Action, ActionId: "increment")];

    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _host = host;
        Publish(context, PluginStateOrigin.Initialization);
        return ValueTask.FromResult(PluginHealth.Ready);
    }
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask ResumeAsync(PluginContext context, CancellationToken token)
    {
        Publish(context, PluginStateOrigin.Initialization);
        return ValueTask.CompletedTask;
    }
    public ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.ActionId != "increment")
            return ValueTask.FromResult(new PluginActionResult(request.OperationId, PluginActionOutcome.Rejected));
        _count++;
        Publish(context, PluginStateOrigin.Action, request.OperationId);
        return ValueTask.FromResult(new PluginActionResult(request.OperationId, PluginActionOutcome.AppliedVerified));
    }
    private void Publish(PluginContext context, PluginStateOrigin origin, Guid? operation = null) =>
        _host?.PublishState(new(context.Instance, context.Generation, ++_sequence, "count",
            new PluginValue(Number: _count), origin, OperationId: operation));
    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken token)
    {
        _host = null;
        return ValueTask.FromResult(true);
    }
    public ValueTask DisposeAsync() { _host = null; return ValueTask.CompletedTask; }
}
'@
$gpuSource = @'
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace ExamplePlugin;

// A graphics driver plugin. WSGM starts it only where a declared display adapter is present. Publish
// descriptors through host.Capabilities for each new CycleGeneration, with roles the manifest declares;
// src/WSGM.Plugin.IntelGpu shows the full pattern. No driver is touched by this example.
public sealed class Plugin : IPlugin, ICapabilityPlugin
{
    public string Id => "__PLUGIN_ID__";
    private ICapabilityHost? _capabilities;

    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _capabilities = host.Capabilities;
        return ValueTask.FromResult(_capabilities is null ? PluginHealth.Unavailable : PluginHealth.Ready);
    }
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command, CancellationToken token) =>
        ValueTask.FromResult(new CapabilityCommandResult
        {
            CommandId = command.CommandId, Outcome = CommandOutcome.Rejected, CompletedAt = DateTimeOffset.UtcNow
        });
    public ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(ApplicationProfileSync sync, CancellationToken token) =>
        ValueTask.FromResult(new ApplicationProfileSyncResult(0, 0, []));
    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken token)
    {
        _capabilities = null;
        return ValueTask.FromResult(true);
    }
    public ValueTask DisposeAsync() { _capabilities = null; return ValueTask.CompletedTask; }
}
'@
$template = if ($gpu) { $gpuSource } else { $source }
[IO.File]::WriteAllText((Join-Path $target 'Plugin.cs'), $template.Replace('__PLUGIN_ID__', $Id))
[IO.File]::WriteAllText((Join-Path $target 'plugin.wsgm.json'), $manifestJson)
Write-Output "Created $target. Build with dotnet build, then package with eng/package-plugin.ps1."
