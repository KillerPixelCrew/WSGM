using System;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Identity;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Plugin.Sdk;
using DeviceStopReason = WSGM.Device.Sdk.Plugin.PluginStopReason;

namespace WSGM.Shell;

/// <summary>Adapts the existing device runtime without replacing its hardware, command or cleanup ownership.</summary>
internal sealed class DevicePluginCompatibilityAdapter(
    DevicePluginRuntime runtime,
    DeviceIdentitySnapshot identity,
    bool controllerManagementEnabled) : IPlugin
{
    private IPluginHost? _host;
    private PluginInstanceIdentity? _instance;
    private bool? _released;
    internal DevicePluginState? LastState { get; private set; }
    internal DeviceStopReason StopReason { get; set; } = DeviceStopReason.WsgmExiting;
    public string Id => runtime.PackageId;

    public async ValueTask<PluginHealth> StartAsync(IPluginHost host, PluginContext context, CancellationToken cancellationToken)
    {
        Validate(context, cancellationToken, starting: true);
        if (_instance is not null) { throw new InvalidOperationException("The device adapter already started."); }
        _host = host;
        _instance = context.Instance;
        runtime.LifecycleStateReceived += OnLifecycleState;
        LastState = await runtime.StartAsync(identity, context.Generation, controllerManagementEnabled, cancellationToken).ConfigureAwait(false);
        return Publish(context, LastState.State);
    }

    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Validate(context, cancellationToken);
        // Desktop/Game transitions do not release the resident Device integration.
        return ValueTask.CompletedTask;
    }

    public async ValueTask SuspendAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Validate(context, cancellationToken);
        LastState = await runtime.SuspendAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        Publish(context, LastState.State);
    }

    public async ValueTask ResumeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        Validate(context, cancellationToken, resuming: true);
        LastState = await runtime.ResumeAsync(context.Generation, context.Deadline, cancellationToken).ConfigureAwait(false);
        Publish(context, LastState.State);
    }

    public async ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        // Cleanup also covers admission canceled before Start and a resume that failed before the
        // runtime advanced its generation. Identity stays exact; cleanup targets the owned runtime.
        Validate(context, cancellationToken, starting: _instance is null, stopping: true);
        if (_released is { } released) { return released; }
        LastState = await runtime.StopAsync(StopReason, context.Deadline, cancellationToken).ConfigureAwait(false);
        _released = LastState.Reason is null;
        return _released.Value;
    }

    public ValueTask DisposeAsync()
    {
        runtime.LifecycleStateReceived -= OnLifecycleState;
        return runtime.DisposeAsync();
    }

    private void Validate(PluginContext context, CancellationToken cancellationToken, bool resuming = false, bool starting = false, bool stopping = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!starting && _instance is null) { throw new InvalidOperationException("The device adapter has not started."); }
        if (context.Instance.PluginId != Id || (_instance is not null && _instance != context.Instance))
        { throw new InvalidOperationException("Device adapter instance identity changed."); }
        if (context.Generation <= 0 || (!stopping && (resuming ? context.Generation <= runtime.CycleGeneration : context.Generation != runtime.CycleGeneration)))
        { throw new InvalidOperationException("Device adapter generation is stale."); }
        if (context.Deadline <= DateTimeOffset.UtcNow) { throw new OperationCanceledException("Device adapter deadline expired."); }
    }

    private PluginHealth Publish(PluginContext context, DeviceCycleState state)
    {
        PluginHealth health = Health(state);
        _host?.PublishHealth(new(context.Instance, context.Generation, health, LastState?.Reason?.Detail));
        return health;
    }

    private static PluginHealth Health(DeviceCycleState state) => state switch
    {
        DeviceCycleState.Active => PluginHealth.Ready,
        DeviceCycleState.Faulted => PluginHealth.Failed,
        _ => PluginHealth.Unavailable,
    };

    private void OnLifecycleState(DevicePluginState state)
    {
        LastState = state;
        if (_instance is { } instance)
        { _host?.PublishHealth(new(instance, state.CycleGeneration, Health(state.State), state.Reason?.Detail)); }
    }
}
