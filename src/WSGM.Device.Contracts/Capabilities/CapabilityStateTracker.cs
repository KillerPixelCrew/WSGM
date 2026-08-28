using System;
using System.Collections.Generic;

namespace WSGM.Device.Contracts.Capabilities;

/// <summary>
/// One capability-state update as it arrives from the plugin.
/// </summary>
/// <param name="Sequence">
/// Monotonic per-host sequence number. Assigned by the producer, never by the consumer.
/// </param>
/// <param name="State">The state being reported.</param>
public sealed record CapabilityStateDelta(long Sequence, CapabilityState State);

/// <summary>Why a delta was not applied.</summary>
public enum DeltaRejection
{
    /// <summary>It was applied.</summary>
    None,

    /// <summary>An update with this sequence number or newer was already applied.</summary>
    OutOfOrder,

    /// <summary>It describes a host generation that has been replaced.</summary>
    StaleHostGeneration,
}

/// <summary>
/// Keeps the latest state per capability, discarding updates that arrive out of order.
/// </summary>
/// <remarks>
/// The high-rate state channel does not promise ordering, and a delayed older sample overwriting a
/// newer one is not a cosmetic glitch: it can restore a "fresh" reading that the device has already
/// moved past, and the UI would then command against it. Sequence numbers are per host generation,
/// so a host restart resets them — which is why the tracker discards anything from a superseded host
/// rather than comparing numbers across the boundary.
/// </remarks>
public sealed class CapabilityStateTracker
{
    private readonly Dictionary<string, CapabilityStateDelta> _latest =
        new(StringComparer.Ordinal);

    private long _hostGeneration;

    /// <summary>Creates a tracker for one host generation.</summary>
    /// <param name="hostGeneration">The host generation whose updates this tracker accepts.</param>
    public CapabilityStateTracker(long hostGeneration) => _hostGeneration = hostGeneration;

    /// <summary>The host generation currently being tracked.</summary>
    public long HostGeneration => _hostGeneration;

    /// <summary>
    /// Applies an update, unless it is older than what is already held.
    /// </summary>
    /// <param name="delta">The update to apply.</param>
    /// <returns><see cref="DeltaRejection.None"/> when applied, otherwise why it was discarded.</returns>
    public DeltaRejection Apply(CapabilityStateDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        if (delta.State.HostGeneration != _hostGeneration)
        {
            return DeltaRejection.StaleHostGeneration;
        }

        string key = Key(delta.State);
        if (_latest.TryGetValue(key, out CapabilityStateDelta? existing)
            && delta.Sequence <= existing.Sequence)
        {
            return DeltaRejection.OutOfOrder;
        }

        _latest[key] = delta;
        return DeltaRejection.None;
    }

    /// <summary>
    /// Starts tracking a new host generation, discarding everything from the previous one.
    /// </summary>
    /// <param name="hostGeneration">The new host generation.</param>
    /// <remarks>
    /// Nothing survives a host restart. The previous host's observations described hardware it no
    /// longer owns, and its sequence numbering has restarted, so carrying anything across would
    /// compare two unrelated counters.
    /// </remarks>
    public void ResetTo(long hostGeneration)
    {
        _latest.Clear();
        _hostGeneration = hostGeneration;
    }

    /// <summary>Returns the latest state for one capability, if any has been reported.</summary>
    /// <param name="capabilityId">The capability identifier.</param>
    /// <param name="instanceId">The instance discriminator, or null.</param>
    /// <returns>The latest state, or <see langword="null"/>.</returns>
    public CapabilityState? Latest(string capabilityId, string? instanceId = null) =>
        _latest.TryGetValue(Key(capabilityId, instanceId), out CapabilityStateDelta? delta)
            ? delta.State
            : null;

    private static string Key(CapabilityState state) => Key(state.CapabilityId, state.InstanceId);

    private static string Key(string capabilityId, string? instanceId) =>
        instanceId is { Length: > 0 } ? $"{capabilityId}#{instanceId}" : capabilityId;
}

/// <summary>
/// Recognises a command intent that has already been applied.
/// </summary>
/// <remarks>
/// Exists for the retry-after-uncertainty case. When a command times out, the caller does not know
/// whether it landed; if it did and the caller retries, the plugin must be able to say "already
/// done" rather than write a second time. Keying on the idempotency key rather than the command ID
/// is what makes that work — a retry is a new command carrying the same intent.
/// </remarks>
public sealed class CommandDeduplicator
{
    private readonly Dictionary<string, CapabilityCommandResult> _completed =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the result of an earlier command with the same intent, if one completed.
    /// </summary>
    /// <param name="idempotencyKey">The intent key carried by the command.</param>
    /// <param name="result">The earlier result, when one exists.</param>
    /// <returns><see langword="true"/> when this intent has already been applied.</returns>
    public bool TryGetCompleted(string idempotencyKey, out CapabilityCommandResult? result) =>
        _completed.TryGetValue(idempotencyKey, out result);

    /// <summary>
    /// Records a completed command so a later retry of the same intent can be recognised.
    /// </summary>
    /// <param name="idempotencyKey">The intent key carried by the command.</param>
    /// <param name="result">The result to remember.</param>
    /// <remarks>
    /// Only outcomes that establish what the hardware did are remembered. An uncertain outcome is
    /// deliberately not recorded: the whole point of retrying it is that nobody knows whether it
    /// landed, so answering a retry with "already done" would assert exactly what is unknown.
    /// </remarks>
    public void Record(string idempotencyKey, CapabilityCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (CommandOutcomeRules.IsUncertain(result.Outcome))
        {
            return;
        }

        _completed[idempotencyKey] = result;
    }

    /// <summary>Forgets every recorded intent, for use when the device generation changes.</summary>
    public void Clear() => _completed.Clear();
}
