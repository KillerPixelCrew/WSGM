using System;

namespace WSGM.Device.Contracts.Lifecycle;

/// <summary>
/// The pure transition function for the device cycle.
/// </summary>
/// <remarks>
/// Kept as a decision function rather than being spread across the host so the cycle's two hardest
/// rules can be tested without a device present:
/// <list type="bullet">
/// <item>Desktop and Game Mode transitions are not inputs here at all. Shell mode changes which UI
/// projection is visible and which per-application profile applies; it never recreates the plugin,
/// reopens devices, resets fans, or rebuilds the virtual controller.</item>
/// <item>A host fault is a state inside the cycle, not a way out of it. Only
/// <see cref="LifecycleTrigger.WsgmExiting"/> and
/// <see cref="LifecycleTrigger.IntegrationDisabled"/> reach <see cref="DeviceCycleState.Disabled"/>,
/// so a crash can never be mistaken for a clean handoff to an external manager.</item>
/// </list>
/// </remarks>
public static class DeviceCycleTransitions
{
    /// <summary>
    /// Returns the state the cycle moves to.
    /// </summary>
    /// <param name="current">The current state.</param>
    /// <param name="trigger">What happened.</param>
    /// <param name="faultsInWindow">Faults already counted in the restart window.</param>
    /// <param name="policy">The restart policy in force.</param>
    /// <returns>The next state, which may be the current one when the trigger does not apply.</returns>
    public static DeviceCycleState Next(
        DeviceCycleState current,
        LifecycleTrigger trigger,
        int faultsInWindow = 0,
        RestartPolicy? policy = null)
    {
        policy ??= RestartPolicy.Default;

        // The two terminal triggers apply from anywhere, because the user's intent to stop must not
        // depend on which state the cycle happens to be in when they express it.
        switch (trigger)
        {
            case LifecycleTrigger.WsgmExiting:
            case LifecycleTrigger.IntegrationDisabled:
                return current is DeviceCycleState.Disabled
                    ? DeviceCycleState.Disabled
                    : DeviceCycleState.Deactivating;

            case LifecycleTrigger.WsgmStarted:
            case LifecycleTrigger.IntegrationEnabled:
                return current is DeviceCycleState.Disabled
                    ? DeviceCycleState.Detected
                    : current;
        }

        return current switch
        {
            DeviceCycleState.Disabled => DeviceCycleState.Disabled,

            DeviceCycleState.Detected => trigger switch
            {
                LifecycleTrigger.HostFaulted => Fault(faultsInWindow, policy),
                _ => DeviceCycleState.Activating,
            },

            DeviceCycleState.Activating or DeviceCycleState.Active or DeviceCycleState.Degraded
                or DeviceCycleState.Passive => trigger switch
                {
                    LifecycleTrigger.HostFaulted => Fault(faultsInWindow, policy),
                    LifecycleTrigger.SystemSuspending => DeviceCycleState.Suspended,

                    // A new device generation re-acquires within the same cycle. The host is fine;
                    // its handles are not.
                    LifecycleTrigger.DeviceGenerationChanged => DeviceCycleState.Activating,
                    _ => current,
                },

            DeviceCycleState.Suspended => trigger switch
            {
                LifecycleTrigger.SystemResumed => DeviceCycleState.Activating,
                LifecycleTrigger.HostFaulted => Fault(faultsInWindow, policy),
                _ => DeviceCycleState.Suspended,
            },

            // Deactivation ends only when the release sequence says it did. A repeated exit request
            // must not restart it, and no unrelated event may cut it short while hardware is still
            // being restored.
            DeviceCycleState.Deactivating => trigger is LifecycleTrigger.DeactivationCompleted
                ? DeviceCycleState.Disabled
                : DeviceCycleState.Deactivating,

            DeviceCycleState.Quarantined => trigger switch
            {
                LifecycleTrigger.ManualRetry => DeviceCycleState.Activating,

                // Deliberately absent: HostRestarted. Quarantine is only left by an explicit human
                // action, a restart of WSGM, or the integration toggle. Automatic recovery would
                // reintroduce the crash loop quarantine exists to stop.
                _ => DeviceCycleState.Quarantined,
            },

            _ => current,
        };
    }

    /// <summary>
    /// Whether a state still owns hardware that has to be released before the cycle can end.
    /// </summary>
    /// <param name="state">The state to classify.</param>
    /// <returns><see langword="true"/> when deactivation has real work to do.</returns>
    public static bool OwnsHardware(DeviceCycleState state) => state
        is DeviceCycleState.Activating
        or DeviceCycleState.Active
        or DeviceCycleState.Degraded
        or DeviceCycleState.Suspended;

    private static DeviceCycleState Fault(int faultsInWindow, RestartPolicy policy) =>
        policy.Evaluate(faultsInWindow, out _) is FaultResponse.Quarantine
            ? DeviceCycleState.Quarantined
            : DeviceCycleState.Activating;
}

/// <summary>
/// A versioned, read-only snapshot of what the host and plugin are doing.
/// </summary>
/// <remarks>
/// Read-only by construction: there is no field here that a consumer could write back, and nothing
/// that carries a transport. Device Lab captures this from a running plugin without stopping or
/// recreating the process-long device cycle.
/// </remarks>
public sealed record DeviceDiagnosticsSnapshot
{
    /// <summary>Schema version of this snapshot.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Selected package.</summary>
    public required string PackageId { get; init; }

    /// <summary>Matched device definition.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Package trust tier, as assigned at install.</summary>
    public required string TrustTier { get; init; }

    /// <summary>Current cycle state.</summary>
    public required DeviceCycleState CycleState { get; init; }

    /// <summary>Current host generation.</summary>
    public required long HostGeneration { get; init; }

    /// <summary>Current device generation.</summary>
    public required long DeviceGeneration { get; init; }

    /// <summary>Host restarts since the cycle began.</summary>
    public int RestartCount { get; init; }

    /// <summary>Per-resource state, keyed by resource identifier.</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, ResourceState> Resources { get; init; }
        = new System.Collections.Generic.Dictionary<string, ResourceState>();

    /// <summary>Journal entries still needing attention.</summary>
    public System.Collections.Generic.IReadOnlyList<RecoveryJournalEntry> OutstandingJournalEntries
    { get; init; } = [];

    /// <summary>When this snapshot was taken, in UTC.</summary>
    public required DateTimeOffset CapturedAt { get; init; }
}
