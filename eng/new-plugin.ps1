# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
Creates a common plugin project with a harmless status/action example.
.DESCRIPTION
Creates a new directory only. The generated project references this checkout's common SDK, and its
manifest takes the SDK's API version and passes the SDK's manifest validation before anything is
written. No package is installed, enabled, or executed by this command.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$Output,
    [string]$Category = 'wsgm.peripheral'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Category -eq 'wsgm.device') { throw 'Use Device Lab scaffold for the Device specialization.' }
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
$manifestJson = [ordered]@{
    id = $Id; name = $Id; version = '0.1.0'; category = $Category
    minimumApiVersion = $apiVersion; maximumApiVersion = $apiVersion
    entryAssembly = 'ExamplePlugin.dll'; entryType = 'ExamplePlugin.Plugin'
    dependencies = @(); permissions = @()
} | ConvertTo-Json -Depth 8
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
[IO.File]::WriteAllText((Join-Path $target 'Plugin.cs'), $source.Replace('__PLUGIN_ID__', $Id))
[IO.File]::WriteAllText((Join-Path $target 'plugin.wsgm.json'), $manifestJson)
Write-Output "Created $target. Build with dotnet build, then package with eng/package-plugin.ps1."
