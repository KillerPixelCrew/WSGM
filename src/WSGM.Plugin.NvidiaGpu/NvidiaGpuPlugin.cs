using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Gpu;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.NvidiaGpu;

/// <summary>NVIDIA driver profiles and output color controls through the driver's installed NVAPI.</summary>
public sealed class NvidiaGpuPlugin : IPlugin, ICapabilityPlugin
{
    private readonly DriverRuntime _runtime = new("wsgm.gpu.nvidia", state => new NvSession(state));
    /// <inheritdoc />
    public string Id => _runtime.Id;
    /// <inheritdoc />
    public ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken) => _runtime.StartAsync(host, context, cancellationToken);
    /// <inheritdoc />
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken) => _runtime.SessionChangedAsync(context, cancellationToken);
    /// <inheritdoc />
    public ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken) => _runtime.SuspendAsync(context, cancellationToken);
    /// <inheritdoc />
    public ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken) => _runtime.ResumeAsync(context, cancellationToken);
    /// <inheritdoc />
    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken) => _runtime.StopAsync(context, cancellationToken);
    /// <inheritdoc />
    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
    /// <inheritdoc />
    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command, CancellationToken cancellationToken) => _runtime.ExecuteCommandAsync(command, cancellationToken);
    /// <inheritdoc />
    public ValueTask<ApplicationProfileSyncResult> SyncApplicationProfilesAsync(ApplicationProfileSync sync, CancellationToken cancellationToken) => _runtime.SyncApplicationProfilesAsync(sync, cancellationToken);
}
