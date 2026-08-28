using System;

namespace WSGM.Device.Contracts.Lifecycle;

/// <summary>
/// What to do about a host that exited unexpectedly.
/// </summary>
public enum FaultResponse
{
    /// <summary>Restart after the computed backoff.</summary>
    Restart,

    /// <summary>Stop restarting and quarantine until a person asks for a retry.</summary>
    Quarantine,
}

/// <summary>
/// Bounded restart, backoff, and quarantine for an unexpectedly exited host.
/// </summary>
/// <remarks>
/// The budget exists because an unrecoverable fault repeats. Without one, a plugin that crashes on
/// activation would reacquire hardware, crash, and reacquire again several times a second — each
/// cycle touching the device and writing journal entries.
/// <para>
/// The window matters as much as the count: faults spread over hours are a flaky transport worth
/// retrying, while three in five minutes is something that will not fix itself.
/// </para>
/// </remarks>
public sealed record RestartPolicy
{
    /// <summary>Faults allowed inside <see cref="Window"/> before quarantine.</summary>
    public int MaxRestarts { get; init; } = 3;

    /// <summary>How far back faults are counted.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay before the first restart. Quadruples for each subsequent one.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the computed backoff.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum interval between manual retries once quarantined.</summary>
    public TimeSpan ManualRetryCooldown { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The default policy.</summary>
    public static RestartPolicy Default { get; } = new();

    /// <summary>
    /// Decides whether to restart after a fault, and how long to wait first.
    /// </summary>
    /// <param name="faultsInWindow">
    /// Faults already recorded inside <see cref="Window"/>, not counting this one.
    /// </param>
    /// <param name="backoff">How long to wait before restarting, when restarting.</param>
    /// <returns>Whether to restart or quarantine.</returns>
    public FaultResponse Evaluate(int faultsInWindow, out TimeSpan backoff)
    {
        if (faultsInWindow >= MaxRestarts)
        {
            backoff = TimeSpan.Zero;
            return FaultResponse.Quarantine;
        }

        // The frozen sequence is 1 s, 4 s, 16 s before quarantine. Computed in ticks rather than by
        // repeated multiplication so a large fault count cannot overflow before the cap applies.
        double multiplier = Math.Pow(4, faultsInWindow);
        double ticks = Math.Min(InitialBackoff.Ticks * multiplier, MaxBackoff.Ticks);

        backoff = TimeSpan.FromTicks((long)ticks);
        return FaultResponse.Restart;
    }
}

/// <summary>
/// Per-phase deadlines for releasing hardware and WSGM-owned state.
/// </summary>
/// <remarks>
/// Four phases, each bounded separately, because they fail for different reasons and one hanging
/// phase must not consume another's budget.
/// <para>
/// A phase that times out is recorded and the sequence <em>continues</em>. That is deliberate: the
/// later phases remove WSGM-owned state — the virtual target and the HidHide entries — and skipping
/// them because the plugin was slow would leave the user's physical controller hidden with nothing
/// left running to un-hide it.
/// </para>
/// </remarks>
public sealed record DeactivationBudget
{
    /// <summary>
    /// WSGM stops accepting commands, establishes input fallback, and neutralizes the virtual target.
    /// HidHide stays in place so the still-captured physical device is not briefly exposed.
    /// </summary>
    public TimeSpan Quiesce { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The plugin stops readers, releases physical-controller acquisition, restores the original
    /// controller mode, and waits for re-enumeration.
    /// </summary>
    public TimeSpan ReleaseController { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The plugin restores remaining hardware state from its snapshots, closes transports, and
    /// finalizes the journal.
    /// </summary>
    public TimeSpan RestoreHardware { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>WSGM removes the virtual target and only its own HidHide entries, then disposes the host.</summary>
    public TimeSpan RemoveWsgmState { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Total nominal budget across all four phases.</summary>
    public TimeSpan Total => Quiesce + ReleaseController + RestoreHardware + RemoveWsgmState;

    /// <summary>Budget for a normal exit or the Settings master toggle.</summary>
    public static DeactivationBudget Normal { get; } = new();

    /// <summary>
    /// Compressed budget for logoff and session end, where Windows will not wait long.
    /// </summary>
    public static DeactivationBudget SessionEnd { get; } = new()
    {
        Quiesce = TimeSpan.FromMilliseconds(500),
        ReleaseController = TimeSpan.FromSeconds(2),
        RestoreHardware = TimeSpan.FromSeconds(2),
        RemoveWsgmState = TimeSpan.FromMilliseconds(500),
    };

    /// <summary>
    /// Whether a hardware write may still be started with this much of the phase left.
    /// </summary>
    /// <param name="remaining">Time left in the current phase.</param>
    /// <param name="expectedDuration">How long the write and its journal update are expected to take.</param>
    /// <returns><see langword="true"/> when the write can complete or be journalled in time.</returns>
    /// <remarks>
    /// Checked before every write during shutdown. Starting one that cannot finish is how a device is
    /// left half-configured with an entry that says only "applying".
    /// </remarks>
    public static bool MayStartWrite(TimeSpan remaining, TimeSpan expectedDuration) =>
        remaining >= expectedDuration;
}
