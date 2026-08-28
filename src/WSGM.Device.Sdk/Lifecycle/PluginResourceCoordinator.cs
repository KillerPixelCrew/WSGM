using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;
using WSGM.Device.Sdk.Plugin;

namespace WSGM.Device.Sdk.Lifecycle;

/// <summary>One independently acquired and restored plugin-owned hardware resource.</summary>
/// <remarks>
/// Implementations keep transports and handles private. The coordinator receives only semantic
/// state, so a failure in one resource can be published without disabling unrelated resources.
/// </remarks>
public interface IPluginResource
{
    /// <summary>Stable identifier from the device definition.</summary>
    string ResourceId { get; }

    /// <summary>Attempts to acquire this resource for one device generation.</summary>
    /// <param name="context">Current generations and operation deadline.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <returns>Owned, passive, degraded, or faulted state.</returns>
    ValueTask<PluginResourceOperationResult> AcquireAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken);

    /// <summary>Quiesces volatile work before suspend or session lock.</summary>
    /// <param name="context">Current generations and operation deadline.</param>
    /// <param name="cancellationToken">Cancels quiescence.</param>
    /// <returns>The resource state after quiescence.</returns>
    ValueTask<PluginResourceOperationResult> SuspendAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken);

    /// <summary>Revalidates and reacquires this resource after resume.</summary>
    /// <param name="context">Current generations and operation deadline.</param>
    /// <param name="cancellationToken">Cancels resumption.</param>
    /// <returns>The resource state after resumption.</returns>
    ValueTask<PluginResourceOperationResult> ResumeAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken);

    /// <summary>Restores and releases this resource.</summary>
    /// <param name="context">Current generations and operation deadline.</param>
    /// <param name="cancellationToken">Cancels waiting for release.</param>
    /// <returns>Idle when release was verified; otherwise an explicit failure state.</returns>
    ValueTask<PluginResourceOperationResult> ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken);
}

/// <summary>Generations and deadline for one resource operation.</summary>
/// <param name="HostGeneration">Host generation that owns the operation.</param>
/// <param name="DeviceGeneration">Device generation that owns opened handles.</param>
/// <param name="Deadline">UTC deadline after which the host stops waiting.</param>
public sealed record PluginResourceOperationContext(
    long HostGeneration,
    long DeviceGeneration,
    DateTimeOffset Deadline);

/// <summary>Semantic outcome of one resource operation.</summary>
/// <param name="State">Current ownership and health.</param>
/// <param name="Reason">Structured reason when the state is not healthy.</param>
public sealed record PluginResourceOperationResult(
    ResourceState State,
    CapabilityReason? Reason = null);

/// <summary>
/// Coordinates resources independently while preserving deterministic acquisition and release order.
/// </summary>
public sealed class PluginResourceCoordinator
{
    private readonly IPluginHostAdapter _host;
    private readonly IReadOnlyList<IPluginResource> _resources;
    private readonly Dictionary<string, ResourceState> _states;
    private readonly IReadOnlyDictionary<string, ResourceState> _statesView;

    /// <summary>Creates a coordinator for one plugin activation.</summary>
    /// <param name="host">Semantic publication adapter.</param>
    /// <param name="resources">Resources in dependency-safe acquisition order.</param>
    public PluginResourceCoordinator(
        IPluginHostAdapter host,
        IReadOnlyList<IPluginResource> resources)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(resources);

        _host = host;
        _resources = [.. resources];
        _states = new Dictionary<string, ResourceState>(StringComparer.Ordinal);
        _statesView = new ReadOnlyDictionary<string, ResourceState>(_states);

        foreach (IPluginResource resource in _resources)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentException.ThrowIfNullOrWhiteSpace(resource.ResourceId);
            if (!_states.TryAdd(resource.ResourceId, ResourceState.Idle))
            {
                throw new ArgumentException(
                    $"Resource '{resource.ResourceId}' is declared more than once.",
                    nameof(resources));
            }
        }
    }

    /// <summary>Current state of every declared resource.</summary>
    public IReadOnlyDictionary<string, ResourceState> States => _statesView;

    /// <summary>Acquires every resource independently in declaration order.</summary>
    /// <param name="context">Current generations and acquisition deadline.</param>
    /// <param name="cancellationToken">Cancels activation and unwinds attempted resources.</param>
    /// <returns>A task completing after every resource was assessed.</returns>
    public async ValueTask ActivateAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var attempted = new List<IPluginResource>();

        try
        {
            foreach (IPluginResource resource in _resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempted.Add(resource);
                await PublishAsync(
                    resource.ResourceId,
                    new PluginResourceOperationResult(ResourceState.Acquiring),
                    context.DeviceGeneration,
                    cancellationToken).ConfigureAwait(false);

                PluginResourceOperationResult result = await InvokeAsync(
                    resource,
                    operation: static (item, operationContext, token) =>
                        item.AcquireAsync(operationContext, token),
                    context,
                    cancellationToken).ConfigureAwait(false);
                await PublishAsync(
                    resource.ResourceId,
                    NormalizeAcquisitionResult(result),
                    context.DeviceGeneration,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseCoreAsync(
                attempted,
                context,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Quiesces each acquired resource without coupling their failure states.</summary>
    /// <param name="context">Current generations and suspend deadline.</param>
    /// <param name="cancellationToken">Cancels quiescence.</param>
    /// <returns>A task completing after all eligible resources were assessed.</returns>
    public ValueTask SuspendAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken = default) =>
        OperateForwardAsync(
            context,
            static (resource, operationContext, token) =>
                resource.SuspendAsync(operationContext, token),
            cancellationToken);

    /// <summary>Revalidates and resumes each declared resource in acquisition order.</summary>
    /// <param name="context">Current generations and resume deadline.</param>
    /// <param name="cancellationToken">Cancels resumption.</param>
    /// <returns>A task completing after all resources were assessed.</returns>
    public ValueTask ResumeAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken = default) =>
        OperateForwardAsync(
            context,
            static (resource, operationContext, token) =>
                resource.ResumeAsync(operationContext, token),
            cancellationToken);

    /// <summary>Restores and releases resources in reverse declaration order.</summary>
    /// <param name="context">Current generations and release deadline.</param>
    /// <param name="cancellationToken">Cancels waiting for release.</param>
    /// <returns>A task completing after every resource reported a terminal state.</returns>
    public ValueTask ReleaseAsync(
        PluginResourceOperationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ReleaseCoreAsync(_resources, context, cancellationToken);
    }

    private async ValueTask OperateForwardAsync(
        PluginResourceOperationContext context,
        ResourceOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (IPluginResource resource in _resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginResourceOperationResult result = await InvokeAsync(
                resource,
                operation,
                context,
                cancellationToken).ConfigureAwait(false);
            await PublishAsync(
                resource.ResourceId,
                result,
                context.DeviceGeneration,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseCoreAsync(
        IEnumerable<IPluginResource> resources,
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        foreach (IPluginResource resource in resources.Reverse())
        {
            await PublishAsync(
                resource.ResourceId,
                new PluginResourceOperationResult(ResourceState.Releasing),
                context.DeviceGeneration,
                CancellationToken.None).ConfigureAwait(false);
            PluginResourceOperationResult result;
            try
            {
                result = await InvokeAsync(
                    resource,
                    operation: static (item, operationContext, token) =>
                        item.ReleaseAsync(operationContext, token),
                    context,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A deadline cannot prove whether restoration completed. Record that explicitly and
                // continue through the remaining resources instead of stranding later resources.
                result = new PluginResourceOperationResult(
                    ResourceState.ReleasedUnverified,
                    new CapabilityReason(
                        CapabilityReasonCode.Quiescing,
                        $"Release of resource '{resource.ResourceId}' exceeded its deadline."));
            }

            await PublishAsync(
                resource.ResourceId,
                NormalizeReleaseResult(result),
                context.DeviceGeneration,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async ValueTask<PluginResourceOperationResult> InvokeAsync(
        IPluginResource resource,
        ResourceOperation operation,
        PluginResourceOperationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(resource, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new PluginResourceOperationResult(
                ResourceState.Faulted,
                new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Resource '{resource.ResourceId}' operation failed: {ex.GetType().Name}."));
        }
    }

    private async ValueTask PublishAsync(
        string resourceId,
        PluginResourceOperationResult result,
        long deviceGeneration,
        CancellationToken cancellationToken)
    {
        _states[resourceId] = result.State;
        await _host.PublishResourceStateAsync(
            new PluginResourceState
            {
                ResourceId = resourceId,
                State = result.State,
                Reason = result.Reason,
                DeviceGeneration = deviceGeneration,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static PluginResourceOperationResult NormalizeAcquisitionResult(
        PluginResourceOperationResult result) => result.State switch
        {
            ResourceState.Owned or ResourceState.Passive or ResourceState.Degraded
                or ResourceState.Faulted => result,
            _ => new PluginResourceOperationResult(
                ResourceState.Faulted,
                new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Acquisition returned invalid state {result.State}.")),
        };

    private static PluginResourceOperationResult NormalizeReleaseResult(
        PluginResourceOperationResult result) => result.State switch
        {
            ResourceState.Idle or ResourceState.ReleasedUnverified or ResourceState.Faulted => result,
            _ => new PluginResourceOperationResult(
                ResourceState.ReleasedUnverified,
                new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Release returned invalid state {result.State}.")),
        };

    private delegate ValueTask<PluginResourceOperationResult> ResourceOperation(
        IPluginResource resource,
        PluginResourceOperationContext context,
        CancellationToken cancellationToken);
}
