using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace ExamplePlugin.Gpu;

// Descriptors belong to each CycleGeneration and the roles the manifest declares.
// src/WSGM.Plugin.IntelGpu shows the full pattern. This example touches no driver.
public sealed class Plugin : IPlugin, ICapabilityPlugin
{
    private ICapabilityHost? _capabilities;

    public string Id => "__PLUGIN_ID__";

    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _capabilities = host.Capabilities;
        return ValueTask.FromResult(_capabilities is null ? PluginHealth.Unavailable : PluginHealth.Ready);
    }

    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken token)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command, CancellationToken token)
    {
        return ValueTask.FromResult(new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            CompletedAt = DateTimeOffset.UtcNow
        });
    }

    public ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(ApplicationProfileSync sync,
        CancellationToken token)
    {
        return ValueTask.FromResult(new ApplicationProfileSyncResult(0, 0, []));
    }

    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken token)
    {
        _capabilities = null;
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        _capabilities = null;
        return ValueTask.CompletedTask;
    }
}
