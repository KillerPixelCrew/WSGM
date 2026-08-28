using System.Text.Json.Serialization;

namespace WSGM.Device.Contracts.Lifecycle;

/// <summary>
/// Where the device cycle is.
/// </summary>
/// <remarks>
/// The cycle spans the whole WSGM run and has exactly two terminal triggers: WSGM exits, or the user
/// turns Device Integration off. Entering or leaving Game Mode, closing a game, restarting Steam,
/// turning controller management off, and a temporarily degraded capability are all state that
/// happens *inside* one cycle — none of them is a transition here.
/// <para>
/// There is deliberately no state meaning "the host crashed". An unexpected host exit is a fault
/// within the running cycle, handled by restart, backoff, and then <see cref="Quarantined"/>. Making
/// it a lifecycle state would let a crash read as an intentional deactivation and a handoff to an
/// external manager that never happened.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceCycleState>))]
public enum DeviceCycleState
{
    /// <summary>Device Integration is off. No host, resource, or hook exists.</summary>
    Disabled,

    /// <summary>The exact board matched. Capabilities are still being probed.</summary>
    Detected,

    /// <summary>
    /// The hardware exists, but another owner or a missing prerequisite prevents acquiring one or
    /// more resources.
    /// </summary>
    Passive,

    /// <summary>Snapshots and asynchronous resource acquisition are in progress.</summary>
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
    /// The host failed repeatedly and will not be restarted automatically.
    /// </summary>
    /// <remarks>
    /// Quarantine fails open: the virtual target and WSGM's HidHide entries are removed so the user
    /// keeps a working controller, while desired state is retained because a fault is not a change of
    /// intent.
    /// </remarks>
    Quarantined,
}

/// <summary>
/// The state of one hardware resource, tracked independently of every other.
/// </summary>
/// <remarks>
/// Per-resource ownership is what keeps a controller conflict from disabling fans, lighting, power,
/// charge, and OEM events. Each resource moves through these states on its own.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ResourceState>))]
public enum ResourceState
{
    /// <summary>Not acquired and not being acquired.</summary>
    Idle,

    /// <summary>Acquisition is in progress.</summary>
    Acquiring,

    /// <summary>Owned and healthy.</summary>
    Owned,

    /// <summary>
    /// Present but not owned, because another writer holds it or a prerequisite is missing.
    /// </summary>
    /// <remarks>
    /// Reached only from a demonstrated conflict — competing writes or an exclusive-access failure.
    /// A matching process or service name is never sufficient: an OEM tool being installed, or even
    /// running, does not establish that it currently owns the hardware.
    /// </remarks>
    Passive,

    /// <summary>Acquired but not fully functional.</summary>
    Degraded,

    /// <summary>Being released and restored.</summary>
    Releasing,

    /// <summary>
    /// Released, but restoration could not be confirmed. Journalled for the next start.
    /// </summary>
    ReleasedUnverified,

    /// <summary>Failed and blocked from further use until recovery.</summary>
    Faulted,
}

/// <summary>
/// Why the device cycle changed state.
/// </summary>
/// <remarks>
/// Kept separate from the state itself so diagnostics can distinguish "the user turned it off" from
/// "the host died", which look identical if you only record the resulting state.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<LifecycleTrigger>))]
public enum LifecycleTrigger
{
    /// <summary>WSGM started with Device Integration enabled.</summary>
    WsgmStarted,

    /// <summary>The user turned Device Integration on.</summary>
    IntegrationEnabled,

    /// <summary>The user turned Device Integration off.</summary>
    IntegrationDisabled,

    /// <summary>WSGM is exiting.</summary>
    WsgmExiting,

    /// <summary>The host exited unexpectedly.</summary>
    HostFaulted,

    /// <summary>The host was restarted after a fault.</summary>
    HostRestarted,

    /// <summary>Repeated faults exhausted the restart budget.</summary>
    RestartBudgetExhausted,

    /// <summary>The machine is suspending or the session is locking.</summary>
    SystemSuspending,

    /// <summary>The machine resumed or the session unlocked.</summary>
    SystemResumed,

    /// <summary>The device re-enumerated, starting a new device generation.</summary>
    DeviceGenerationChanged,

    /// <summary>The user asked to retry after quarantine.</summary>
    ManualRetry,

    /// <summary>
    /// Every deactivation phase finished, cleanly or by timeout.
    /// </summary>
    /// <remarks>
    /// The only way out of <see cref="DeviceCycleState.Deactivating"/>. A repeated exit request does
    /// not end deactivation, and neither does an unrelated event: the cycle ends when the release
    /// sequence says it did, which is also true when a phase timed out and the result was recorded as
    /// an unverified handoff.
    /// </remarks>
    DeactivationCompleted,
}
