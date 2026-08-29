namespace WSGM.Device.Sdk.Capabilities;

/// <summary>One capability-state update as it arrives from the plugin.</summary>
/// <param name="Sequence">Monotonic per-host sequence assigned by the producer.</param>
/// <param name="State">The state being reported.</param>
public sealed record CapabilityStateDelta(long Sequence, CapabilityState State);
