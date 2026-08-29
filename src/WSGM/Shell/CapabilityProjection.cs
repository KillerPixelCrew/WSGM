using System;
using System.Text.Json.Serialization;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>
/// Which desired-state layer supplied the value WSGM is asking for.
/// </summary>
/// <remarks>
/// Ordered lowest to highest precedence. Captured hardware state is deliberately absent: it is
/// restoration-only and never competes with what the user asked for, because adopting an observed
/// value as a desired one would silently turn whatever the device happened to be doing into policy.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<DesiredValueSource>))]
public enum DesiredValueSource
{
    /// <summary>No layer has a value; the device keeps whatever it has.</summary>
    None,

    /// <summary>The per-capability global default for this device.</summary>
    GlobalDefault,

    /// <summary>An AC or DC policy for the current power source.</summary>
    PowerSourcePolicy,

    /// <summary>The selected named hardware profile.</summary>
    HardwareProfile,

    /// <summary>An override for the running application.</summary>
    ApplicationOverride,

    /// <summary>A session-only value that outranks every persistent layer until it is dropped.</summary>
    TemporaryRequest,
}

/// <summary>Where a capability's UI is in the request cycle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandProgress>))]
public enum CommandProgress
{
    /// <summary>Nothing is in flight.</summary>
    Idle,

    /// <summary>A command has been sent and no result has come back.</summary>
    Pending,

    /// <summary>The last command finished cleanly.</summary>
    Completed,

    /// <summary>The last command failed or was refused.</summary>
    Failed,

    /// <summary>The last command finished without establishing what the hardware did.</summary>
    Uncertain,
}

/// <summary>
/// WSGM's view of one capability: what the plugin last reported, plus what WSGM wants and is doing
/// about it.
/// </summary>
/// <remarks>
/// The split from <see cref="CapabilityState"/> is the point. The plugin owns observation; WSGM owns
/// intent. Keeping intent out of the plugin's message is what stops a device that happens to boot at
/// 15 W from being treated as though the user chose 15 W.
/// </remarks>
public sealed record CapabilityProjection
{
    /// <summary>The last state the plugin reported.</summary>
    public required CapabilityState State { get; init; }

    /// <summary>The value WSGM wants, or null when no layer supplies one.</summary>
    public CapabilityValue? DesiredValue { get; init; }

    /// <summary>Which layer supplied <see cref="DesiredValue"/>.</summary>
    public DesiredValueSource DesiredSource { get; init; } = DesiredValueSource.None;

    /// <summary>The value of an in-flight request, shown while a command is pending.</summary>
    public CapabilityValue? PendingValue { get; init; }

    /// <summary>Where the UI is in the request cycle.</summary>
    public CommandProgress Progress { get; init; } = CommandProgress.Idle;

    /// <summary>
    /// Whether the desired value is outside what the current descriptor accepts.
    /// </summary>
    /// <remarks>
    /// Set after a descriptor generation change narrowed a range. The persisted value is kept rather
    /// than clamped: silently moving a user's 30 W request to 25 W because firmware changed would be
    /// a decision made on their behalf and never surfaced.
    /// </remarks>
    public bool DesiredValueOutOfRange { get; init; }
}

/// <summary>
/// How long an observation stays usable.
/// </summary>
/// <remarks>
/// Freshness is per capability because the underlying facts age at wildly different rates: a fan RPM
/// is stale within seconds, while a charge limit changes only when someone changes it. One global
/// timeout would either spam a slow transport or leave a fast-moving reading looking current long
/// after it stopped being so.
/// </remarks>
public sealed record FreshnessPolicy
{
    /// <summary>How long after observation a value is still considered current.</summary>
    public required TimeSpan MaxAge { get; init; }

    /// <summary>
    /// Freshness for a value that only changes when something changes it, such as a charge limit.
    /// </summary>
    public static FreshnessPolicy Settings { get; } = new() { MaxAge = TimeSpan.FromMinutes(5) };

    /// <summary>Freshness for a value that drifts on its own, such as a power limit under a scenario.</summary>
    public static FreshnessPolicy Control { get; } = new() { MaxAge = TimeSpan.FromSeconds(30) };

    /// <summary>Freshness for a live reading, such as fan RPM or temperature.</summary>
    public static FreshnessPolicy Telemetry { get; } = new() { MaxAge = TimeSpan.FromSeconds(5) };
}

/// <summary>
/// Applies the freshness and generation rules that decide whether a reported state may still be
/// shown as current and commanded against.
/// </summary>
public static class CapabilityFreshness
{
    /// <summary>
    /// Returns the state as it should be presented now, downgrading it to
    /// <see cref="HardwareStateQuality.Stale"/> when it can no longer be trusted.
    /// </summary>
    /// <param name="state">The last state reported by the plugin.</param>
    /// <param name="policy">The freshness policy for this capability.</param>
    /// <param name="now">Current time, in UTC.</param>
    /// <param name="currentCycleGeneration">The process/reconnect cycle generation in effect now.</param>
    /// <returns>The state, unchanged or downgraded to stale with a structured reason.</returns>
    public static CapabilityState Evaluate(
        CapabilityState state,
        FreshnessPolicy policy,
        DateTimeOffset now,
        long currentCycleGeneration)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);

        // A faulted capability is already saying something stronger than "old". Downgrading it to
        // stale would lose the fault.
        if (state.Quality is HardwareStateQuality.Faulted or HardwareStateQuality.Unknown)
        {
            return state;
        }

        // A generation change invalidates the observation outright, regardless of age: the handles
        // and the hardware state it described belong to a device that no longer exists.
        if (state.CycleGeneration != currentCycleGeneration)
        {
            return Stale(state, CapabilityReasonCode.GenerationChanged,
                "Observed under a previous process/reconnect cycle.");
        }

        if (state.ObservedAt is not { } observedAt || now - observedAt > policy.MaxAge)
        {
            return Stale(state, CapabilityReasonCode.ObservationExpired,
                $"Observation is older than {policy.MaxAge}.");
        }

        return state;
    }

    /// <summary>
    /// Whether a command may be issued against this state.
    /// </summary>
    /// <param name="state">The state as returned by <see cref="Evaluate"/>.</param>
    /// <returns><see langword="false"/> when the capability is unavailable or its state is not current.</returns>
    /// <remarks>
    /// Commanding from stale state is how a UI sends a value derived from a reading that no longer
    /// describes the device. The control is disabled until a fresh observation arrives.
    /// </remarks>
    public static bool CanCommand(CapabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Available
            && state.Quality is HardwareStateQuality.Observed or HardwareStateQuality.Verified;
    }

    private static CapabilityState Stale(
        CapabilityState state,
        CapabilityReasonCode code,
        string detail) =>
        state with
        {
            Quality = HardwareStateQuality.Stale,
            Available = false,
            Reason = new CapabilityReason(code, detail, Retryable: true),
        };
}
