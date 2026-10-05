using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>One capability-state update as it arrives from the plugin.</summary>
/// <param name="Sequence">Monotonic per-publisher sequence assigned by the producer.</param>
/// <param name="State">The state being reported.</param>
internal sealed record CapabilityStateDelta(long Sequence, CapabilityState State);
