using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Lifecycle;

namespace WSGM.Device.Sdk.Capabilities;

/// <summary>Current hardware facts re-read immediately before one command.</summary>
/// <remarks>
/// A snapshot is deliberately produced by a callback for each command. Caching it in the registry
/// would make the SDK validate against the state that existed when descriptors were published—the
/// stale-write failure this type exists to prevent.
/// </remarks>
public sealed record PluginCommandSnapshot
{
    /// <summary>Whether the exact board and device definition still match.</summary>
    public required bool IdentityVerified { get; init; }

    /// <summary>Whether current firmware is inside the implementation's verified gate.</summary>
    public required bool FirmwareVerified { get; init; }

    /// <summary>Current ownership and health of the capability's resource.</summary>
    public required ResourceState ResourceState { get; init; }

    /// <summary>Current descriptor generation.</summary>
    public required long DescriptorGeneration { get; init; }

    /// <summary>Current device generation.</summary>
    public required long DeviceGeneration { get; init; }

    /// <summary>Whether the machine is currently on AC power.</summary>
    public required bool OnAcPower { get; init; }

    /// <summary>Current observed value, when readback exists.</summary>
    public CapabilityValue? CurrentValue { get; init; }
}

/// <summary>Produces current identity, firmware, ownership, range, and state for every command.</summary>
/// <param name="cancellationToken">Cancels the revalidation read.</param>
/// <returns>Fresh facts; never a cached activation snapshot.</returns>
public delegate ValueTask<PluginCommandSnapshot> PluginCommandRevalidator(
    CancellationToken cancellationToken);

/// <summary>Applies a command that passed both semantic and current-hardware validation.</summary>
/// <param name="execution">Admitted command plus the fresh facts it was admitted against.</param>
/// <param name="cancellationToken">Cancels application and readback.</param>
/// <returns>The truthful apply, readback, and rollback result.</returns>
public delegate ValueTask<CapabilityCommandResult> PluginCommandHandler(
    PluginCommandExecution execution,
    CancellationToken cancellationToken);

/// <summary>A command plus the current hardware facts checked immediately before it.</summary>
/// <param name="Command">Semantic command from WSGM.</param>
/// <param name="Snapshot">Fresh plugin-owned safety snapshot.</param>
public sealed record PluginCommandExecution(
    CapabilityCommand Command,
    PluginCommandSnapshot Snapshot);

/// <summary>
/// Couples one descriptor to mandatory current-state revalidation and its plugin-owned handler.
/// </summary>
public sealed class CapabilityRegistration
{
    private readonly PluginCommandRevalidator _revalidate;
    private readonly PluginCommandHandler _handler;

    /// <summary>Creates one safe capability registration.</summary>
    /// <param name="resourceId">Resource that must still be owned when a command runs.</param>
    /// <param name="descriptor">Immutable semantic descriptor.</param>
    /// <param name="revalidate">Mandatory per-command hardware revalidation.</param>
    /// <param name="handler">Plugin-owned apply/readback/rollback implementation.</param>
    public CapabilityRegistration(
        string resourceId,
        CapabilityDescriptor descriptor,
        PluginCommandRevalidator revalidate,
        PluginCommandHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(revalidate);
        ArgumentNullException.ThrowIfNull(handler);

        ResourceId = resourceId;
        Descriptor = descriptor;
        _revalidate = revalidate;
        _handler = handler;
    }

    /// <summary>Resource that owns this capability's hardware path.</summary>
    public string ResourceId { get; }

    /// <summary>Immutable descriptor published to WSGM.</summary>
    public CapabilityDescriptor Descriptor { get; }

    internal async ValueTask<CapabilityCommandResult> ExecuteAsync(
        CapabilityCommand command,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        PluginCommandSnapshot snapshot;
        try
        {
            snapshot = await _revalidate(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Reject(command, CapabilityReasonCode.Quiescing,
                "Command was cancelled before hardware application began.", now);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Reject(command, CapabilityReasonCode.TransportFaulted,
                $"Current-state revalidation failed: {ex.GetType().Name}.", now);
        }

        CapabilityReason? resourceRefusal = RefusalFor(snapshot);
        if (resourceRefusal is not null)
        {
            return Reject(command, resourceRefusal, now);
        }

        CommandAdmission.Result admission = CommandAdmission.Evaluate(
            command,
            Descriptor,
            snapshot.DescriptorGeneration,
            snapshot.DeviceGeneration,
            snapshot.OnAcPower,
            now);

        if (!admission.Admitted)
        {
            return Reject(command, admission.Reason!, now);
        }

        CapabilityCommandResult result;
        try
        {
            result = await _handler(
                new PluginCommandExecution(command, snapshot),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The handler had control, so cancellation cannot prove whether a write reached the
            // device. Indeterminate is the only honest result.
            return Indeterminate(command,
                "Command was cancelled after hardware application began.",
                timeProvider.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Indeterminate(command,
                $"Capability handler failed after admission: {ex.GetType().Name}.",
                timeProvider.GetUtcNow());
        }

        return NormalizeResult(command, result);
    }

    private static CapabilityReason? RefusalFor(PluginCommandSnapshot snapshot)
    {
        if (!snapshot.IdentityVerified)
        {
            return new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Exact device identity no longer matches this implementation.",
                Retryable: true);
        }

        if (!snapshot.FirmwareVerified)
        {
            return new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "Current firmware is outside this implementation's verified gate.");
        }

        return snapshot.ResourceState switch
        {
            ResourceState.Owned => null,
            ResourceState.Passive => new CapabilityReason(
                CapabilityReasonCode.ResourceConflict,
                "The resource is held by another owner.", Retryable: true),
            ResourceState.Releasing => new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "The resource is being released."),
            ResourceState.Degraded => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The resource is degraded and cannot accept commands."),
            ResourceState.Faulted or ResourceState.ReleasedUnverified => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The resource is faulted or its release could not be verified."),
            _ => new CapabilityReason(
                CapabilityReasonCode.ResourceReleased,
                "The plugin does not currently own this resource.", Retryable: true),
        };
    }

    private static CapabilityCommandResult NormalizeResult(
        CapabilityCommand command,
        CapabilityCommandResult result)
    {
        if (result.CommandId != command.CommandId)
        {
            return Indeterminate(command,
                "Capability handler returned a result for a different command.",
                result.CompletedAt);
        }

        if (result.Outcome is CommandOutcome.AppliedVerified && result.ReadbackValue is null)
        {
            // Never let a plugin's missing readback become verified state merely because the enum
            // said so. The write may be real; the verification is not.
            return result with
            {
                Outcome = CommandOutcome.AppliedUnverified,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    "Handler claimed verified application without readback evidence."),
            };
        }

        if (result.Outcome is not CommandOutcome.AppliedVerified && result.ReadbackValue is not null)
        {
            return result with { ReadbackValue = null };
        }

        return result;
    }

    private static CapabilityCommandResult Reject(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        DateTimeOffset completedAt) =>
        Reject(command, new CapabilityReason(code, detail), completedAt);

    private static CapabilityCommandResult Reject(
        CapabilityCommand command,
        CapabilityReason reason,
        DateTimeOffset completedAt) => new()
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = reason,
            CompletedAt = completedAt,
        };

    private static CapabilityCommandResult Indeterminate(
        CapabilityCommand command,
        string detail,
        DateTimeOffset completedAt) => new()
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail),
            Rollback = RollbackResult.RestoredUnverified,
            CompletedAt = completedAt,
        };
}

/// <summary>Routes semantic commands through registered mandatory revalidation.</summary>
public sealed class CapabilityRegistry
{
    private readonly Dictionary<CapabilityKey, CapabilityRegistration> _registrations;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a registry and rejects duplicate capability instances.</summary>
    /// <param name="descriptorGeneration">Generation of the complete descriptor set.</param>
    /// <param name="deviceGeneration">Device generation the descriptors describe.</param>
    /// <param name="registrations">Capability registrations.</param>
    /// <param name="timeProvider">Clock used for deterministic deadline tests.</param>
    public CapabilityRegistry(
        long descriptorGeneration,
        long deviceGeneration,
        IReadOnlyList<CapabilityRegistration> registrations,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if (descriptorGeneration < 0 || deviceGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptorGeneration),
                "Descriptor and device generations cannot be negative.");
        }

        DescriptorSet = new CapabilityDescriptorSet
        {
            Generation = descriptorGeneration,
            DeviceGeneration = deviceGeneration,
            Descriptors = [.. registrations.Select(registration => registration.Descriptor)],
        };
        _timeProvider = timeProvider ?? TimeProvider.System;
        _registrations = [];

        foreach (CapabilityRegistration registration in registrations)
        {
            CapabilityKey key = new(
                registration.Descriptor.CapabilityId,
                registration.Descriptor.InstanceId);
            if (!_registrations.TryAdd(key, registration))
            {
                throw new ArgumentException(
                    $"Capability '{key}' is registered more than once.",
                    nameof(registrations));
            }
        }
    }

    /// <summary>Complete immutable descriptor set published for this registry.</summary>
    public CapabilityDescriptorSet DescriptorSet { get; }

    /// <summary>Executes a command through its mandatory revalidation gate.</summary>
    /// <param name="command">Command to route.</param>
    /// <param name="cancellationToken">Cancels revalidation or application.</param>
    /// <returns>Rejected when no registration exists; otherwise the handler's normalized outcome.</returns>
    public ValueTask<CapabilityCommandResult> ExecuteAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        CapabilityKey key = new(command.CapabilityId, command.InstanceId);
        return _registrations.TryGetValue(key, out CapabilityRegistration? registration)
            ? registration.ExecuteAsync(command, _timeProvider, cancellationToken)
            : ValueTask.FromResult(new CapabilityCommandResult
            {
                CommandId = command.CommandId,
                Outcome = CommandOutcome.Rejected,
                Reason = new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    $"Capability '{key}' is not registered."),
                CompletedAt = _timeProvider.GetUtcNow(),
            });
    }

    private readonly record struct CapabilityKey(string CapabilityId, string? InstanceId)
    {
        public override string ToString() => InstanceId is null
            ? CapabilityId
            : $"{CapabilityId}/{InstanceId}";
    }
}
