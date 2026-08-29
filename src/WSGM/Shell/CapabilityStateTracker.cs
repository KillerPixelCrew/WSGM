using System;
using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Why a delta was not applied.</summary>
public enum DeltaRejection
{
    /// <summary>It was applied.</summary>
    None,

    /// <summary>An update with this sequence number or newer was already applied.</summary>
    OutOfOrder,

    /// <summary>It describes a process/reconnect cycle that has been replaced.</summary>
    StaleGeneration,
}

/// <summary>
/// Keeps the latest state per capability, discarding updates that arrive out of order.
/// </summary>
/// <remarks>
/// The high-rate state channel does not promise ordering, and a delayed older sample overwriting a
/// newer one is not a cosmetic glitch: it can restore a "fresh" reading that the device has already
/// moved past, and the UI would then command against it. Sequence numbers are per cycle generation,
/// so a host restart resets them — which is why the tracker discards anything from a superseded cycle
/// rather than comparing numbers across the boundary.
/// </remarks>
public sealed class CapabilityStateTracker
{
    private readonly Dictionary<string, CapabilityStateDelta> _latest =
        new(StringComparer.Ordinal);

    private long _cycleGeneration;

    /// <summary>Creates a tracker for one process/reconnect cycle generation.</summary>
    /// <param name="cycleGeneration">The cycle generation whose updates this tracker accepts.</param>
    public CapabilityStateTracker(long cycleGeneration) => _cycleGeneration = cycleGeneration;

    /// <summary>The process/reconnect cycle generation currently being tracked.</summary>
    public long CycleGeneration => _cycleGeneration;

    /// <summary>
    /// Applies an update, unless it is older than what is already held.
    /// </summary>
    /// <param name="delta">The update to apply.</param>
    /// <returns><see cref="DeltaRejection.None"/> when applied, otherwise why it was discarded.</returns>
    public DeltaRejection Apply(CapabilityStateDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        if (delta.State.CycleGeneration != _cycleGeneration)
        {
            return DeltaRejection.StaleGeneration;
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
    /// Starts tracking a new cycle generation, discarding everything from the previous one.
    /// </summary>
    /// <param name="cycleGeneration">The new cycle generation.</param>
    /// <remarks>
    /// Nothing survives a host restart. The previous host's observations described hardware it no
    /// longer owns, and its sequence numbering has restarted, so carrying anything across would
    /// compare two unrelated counters.
    /// </remarks>
    public void ResetTo(long cycleGeneration)
    {
        _latest.Clear();
        _cycleGeneration = cycleGeneration;
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
