using System.Text.Json.Serialization;

namespace WSGM.Device.Sdk.Lifecycle;

/// <summary>
/// Where the device cycle is.
/// </summary>
/// <remarks>
/// The cycle spans the whole WSGM run and has exactly two terminal triggers: WSGM exits, or the user
/// turns Device Integration off. Entering or leaving Game Mode, closing a game, restarting Steam,
/// turning controller management off, and a temporarily degraded capability are all state that
/// happens *inside* one cycle — none of them is a transition here.
/// <para>
/// There is no separate state for a runtime task fault. It is handled within the running cycle by
/// cleanup, bounded restart, and then <see cref="Faulted"/>; it is not an intentional deactivation
/// or a handoff to another manager.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceCycleState>))]
public enum DeviceCycleState
{
    /// <summary>Device Integration is off. No plugin runtime, device service, or hook exists.</summary>
    Disabled,

    /// <summary>The exact board matched. Capabilities are still being probed.</summary>
    Detected,

    /// <summary>
    /// The hardware exists, but another owner or a missing prerequisite prevents acquiring one or
    /// more resources.
    /// </summary>
    Passive,

    /// <summary>Snapshots and device-service startup are in progress.</summary>
    Activating,

    /// <summary>At least one capability is owned and healthy.</summary>
    Active,

    /// <summary>Some capabilities failed; the healthy ones remain usable.</summary>
    Degraded,

    /// <summary>Writes, samples, rumble, and hooks are quiesced for sleep or session transition.</summary>
    Suspended,

    /// <summary>New commands are refused while owned state is released and restored.</summary>
    Deactivating,

    /// <summary>
    /// The plugin runtime failed repeatedly and will not be restarted automatically.
    /// </summary>
    /// <remarks>
    /// This state fails open: the virtual target and WSGM's HidHide entries are removed so the user
    /// keeps a working controller, while desired state is retained because a fault is not a change of
    /// intent.
    /// </remarks>
    Faulted,
}
