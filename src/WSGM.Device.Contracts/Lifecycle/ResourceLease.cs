using System;
using System.Text.Json.Serialization;

namespace WSGM.Device.Contracts.Lifecycle;

/// <summary>
/// What a lease entitles its holder to do with a resource.
/// </summary>
/// <remarks>
/// A lease is a claim on a resource, never a handle to it. Nothing in this contract carries a
/// transport: no HID handle, no WMI scope, no file handle, no device path crosses the IPC boundary.
/// Device Lab asking for a diagnostic session receives observations the plugin produced, not the
/// means to produce its own — which is what keeps "read-only session" a property of the protocol
/// rather than a promise about the caller.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<LeaseKind>))]
public enum LeaseKind
{
    /// <summary>The production plugin owning a resource for normal operation.</summary>
    Production,

    /// <summary>
    /// A bounded, read-only diagnostic session over a resource the production plugin already owns.
    /// </summary>
    /// <remarks>
    /// Served by the running plugin, which forwards observations. It never stops, recreates,
    /// activates, or deactivates the process-long device cycle: a diagnostic read must not be able to
    /// disturb the thing it is diagnosing.
    /// </remarks>
    Diagnostic,

    /// <summary>
    /// An exclusive experiment lease for a Device Lab trial.
    /// </summary>
    /// <remarks>
    /// Requires the production plugin to release that one resource in an orderly way first. Device
    /// Lab never races the running plugin for a resource, and never silently disables Device
    /// Integration to get one.
    /// </remarks>
    Experiment,
}

/// <summary>Why a lease request was refused.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LeaseRefusal>))]
public enum LeaseRefusal
{
    /// <summary>Granted.</summary>
    None,

    /// <summary>The resource is not declared by the active device definition.</summary>
    UnknownResource,

    /// <summary>Another lease of an incompatible kind is active.</summary>
    Conflict,

    /// <summary>An experiment lease was requested while the production holder has not released.</summary>
    ProductionHolderActive,

    /// <summary>The device cycle is quiescing and takes no new leases.</summary>
    Quiescing,

    /// <summary>The resource is quarantined after a failed restoration.</summary>
    Quarantined,

    /// <summary>The request was cancelled before it was granted.</summary>
    Cancelled,
}

/// <summary>A request to hold a resource.</summary>
/// <param name="ResourceId">The resource being claimed.</param>
/// <param name="Kind">What the holder intends to do.</param>
/// <param name="HolderId">Who is asking, for diagnostics and conflict reporting.</param>
/// <param name="Deadline">When the request stops being worth granting, in UTC.</param>
public sealed record LeaseRequest(
    string ResourceId,
    LeaseKind Kind,
    string HolderId,
    DateTimeOffset Deadline);

/// <summary>The outcome of a lease request.</summary>
/// <param name="Granted">Whether the lease was granted.</param>
/// <param name="Refusal">Why not, when it was refused.</param>
/// <param name="ConflictingHolder">Who holds it instead, when the refusal was a conflict.</param>
public sealed record LeaseGrant(
    bool Granted,
    LeaseRefusal Refusal = LeaseRefusal.None,
    string? ConflictingHolder = null);

/// <summary>
/// Decides whether a lease may be granted alongside what is already held.
/// </summary>
public static class LeaseArbitration
{
    /// <summary>
    /// Whether a new lease can coexist with the current holder.
    /// </summary>
    /// <param name="current">The kind currently held, or null when the resource is free.</param>
    /// <param name="requested">The kind being requested.</param>
    /// <returns>Whether the request may be granted.</returns>
    /// <remarks>
    /// Two rules. A diagnostic session coexists with production, because it only reads what the
    /// production plugin already observes. An experiment never coexists with anything: it mutates
    /// hardware, and a second reader mid-trial would make the trial's own observation unreliable —
    /// which is the one thing a trial cannot afford, since its observation is the evidence.
    /// </remarks>
    public static bool CanCoexist(LeaseKind? current, LeaseKind requested) => (current, requested) switch
    {
        (null, _) => true,
        (LeaseKind.Production, LeaseKind.Diagnostic) => true,
        (LeaseKind.Diagnostic, LeaseKind.Diagnostic) => true,
        (LeaseKind.Diagnostic, LeaseKind.Production) => true,
        _ => false,
    };

    /// <summary>
    /// Evaluates a lease request against the current holder.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="currentKind">The kind currently held, or null when free.</param>
    /// <param name="currentHolder">Who holds it, when anyone does.</param>
    /// <param name="resourceState">The resource's current state.</param>
    /// <param name="now">Current time, in UTC.</param>
    /// <returns>The grant or the refusal.</returns>
    public static LeaseGrant Evaluate(
        LeaseRequest request,
        LeaseKind? currentKind,
        string? currentHolder,
        ResourceState resourceState,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Deadline <= now)
        {
            return new LeaseGrant(false, LeaseRefusal.Cancelled);
        }

        if (resourceState is ResourceState.Faulted)
        {
            return new LeaseGrant(false, LeaseRefusal.Quarantined);
        }

        if (resourceState is ResourceState.Releasing)
        {
            return new LeaseGrant(false, LeaseRefusal.Quiescing);
        }

        if (CanCoexist(currentKind, request.Kind))
        {
            return new LeaseGrant(true);
        }

        // An experiment blocked by the production holder gets its own refusal, because the fix is
        // specific: the production plugin must release that one resource in an orderly way first.
        LeaseRefusal refusal = request.Kind is LeaseKind.Experiment && currentKind is LeaseKind.Production
            ? LeaseRefusal.ProductionHolderActive
            : LeaseRefusal.Conflict;

        return new LeaseGrant(false, refusal, currentHolder);
    }
}
