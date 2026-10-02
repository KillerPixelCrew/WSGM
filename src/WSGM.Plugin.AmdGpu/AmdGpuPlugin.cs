// SPDX-License-Identifier: MIT

using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.AmdGpu;

/// <summary>AMD Radeon 3D, output color, scaling and FreeSync through the installed driver.</summary>
public sealed class AmdGpuPlugin : IPlugin, ICapabilityPlugin
{
    private readonly DriverRuntime _runtime = new("wsgm.gpu.amd", (_, report) => new AdlxSession(report));

    /// <inheritdoc />
    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        return _runtime.ExecuteCommandAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(ApplicationProfileSync sync,
        CancellationToken cancellationToken)
    {
        return _runtime.SyncApplicationProfilesAsync(sync, cancellationToken);
    }

    /// <inheritdoc />
    public string Id => _runtime.Id;

    /// <inheritdoc />
    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context,
        CancellationToken cancellationToken)
    {
        return _runtime.StartAsync(host, context, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return _runtime.SessionChangedAsync(context, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return _runtime.SuspendAsync(context, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return _runtime.ResumeAsync(context, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        return _runtime.StopAsync(context, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _runtime.DisposeAsync();
    }
}
