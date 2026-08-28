using System;
using System.Collections.Generic;
using System.Linq;
using WSGM.Device.Contracts.Identity;

namespace WSGM.Device.Contracts.Lifecycle;

/// <summary>
/// Follows one physical endpoint across removal and re-enumeration.
/// </summary>
/// <remarks>
/// Continuation is keyed on <see cref="UsbEndpointObservation.LocationPath"/> — the physical port the
/// device is plugged into — and on nothing else. That choice is forced by measurement rather than
/// preference:
/// <list type="bullet">
/// <item><b>Container ID is unusable.</b> On the reference handheld every relevant device reports the
/// well-known null container GUID, identically, so it carries no grouping information at all.</item>
/// <item><b>The USB serial is unusable.</b> The controller reports an <c>iSerialNumber</c> in XInput
/// mode only; in DirectInput mode it enumerates with a hub/port instance ID instead. Keying on the
/// serial would fail on the first controller-mode switch — which the plugin performs during normal
/// activation.</item>
/// <item><b>The product ID changes on purpose.</b> Switching modes is how the rear paddles become
/// visible, and it changes the PID. Identity cannot be the anchor for an operation whose whole
/// purpose is to change identity.</item>
/// </list>
/// The location path was verified byte-identical before and after a full switch-and-restore cycle,
/// making it the only stable anchor on that hardware.
/// </remarks>
public static class DeviceContinuity
{
    /// <summary>
    /// Finds the endpoint that continues <paramref name="previous"/> after re-enumeration.
    /// </summary>
    /// <param name="previous">The endpoint as observed before the device disappeared.</param>
    /// <param name="current">Endpoints observed after it came back.</param>
    /// <returns>The continuing endpoint, or <see langword="null"/> when none matches.</returns>
    /// <remarks>
    /// Returning null starts a new device generation, which invalidates every handle and marks all
    /// affected state stale. That is the correct outcome for an unmatched endpoint: continuing to use
    /// handles from a device that may not be the same one is worse than re-acquiring.
    /// </remarks>
    public static UsbEndpointObservation? FindContinuation(
        UsbEndpointObservation previous,
        IReadOnlyList<UsbEndpointObservation> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (previous.LocationPath is not { Length: > 0 } location)
        {
            // With no location path there is no sound way to claim continuity. Identity fields are
            // exactly the ones a mode switch changes, so guessing here would produce a confident
            // wrong answer instead of an honest re-acquisition.
            return null;
        }

        return current.FirstOrDefault(endpoint =>
            string.Equals(endpoint.LocationPath, location, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether an observed endpoint set continues the previous device generation.
    /// </summary>
    /// <param name="previous">Endpoints from the previous generation.</param>
    /// <param name="current">Endpoints observed now.</param>
    /// <returns>
    /// <see langword="true"/> when every previously present endpoint that is not detachable has a
    /// continuation.
    /// </returns>
    /// <remarks>
    /// Detachable endpoints are excluded because their absence is normal — a removable controller
    /// half being detached is not a new device. A non-detachable endpoint vanishing is.
    /// </remarks>
    public static bool ContinuesGeneration(
        IReadOnlyList<UsbEndpointObservation> previous,
        IReadOnlyList<UsbEndpointObservation> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        return previous.All(endpoint => FindContinuation(endpoint, current) is not null);
    }
}

/// <summary>
/// Where the recovery journal lives and how it is maintained.
/// </summary>
/// <remarks>
/// Recorded as policy rather than scattered through the host so the rules are reviewable in one
/// place. The file mechanics belong to the host; these are the decisions it must implement.
/// </remarks>
public sealed record JournalPolicy
{
    /// <summary>
    /// Journal directory, relative to the WSGM per-user data directory.
    /// </summary>
    /// <remarks>
    /// Per user rather than machine-wide: the journal describes hardware changes made by one user's
    /// session, and a machine-wide file would let one user's session reconcile another's.
    /// </remarks>
    public string RelativeDirectory { get; init; } = "device/journal";

    /// <summary>
    /// Whether writes must be atomic replacements rather than in-place edits.
    /// </summary>
    /// <remarks>
    /// Always true, and load-bearing: the journal is read after a crash, so a torn write is exactly
    /// the failure it must survive. Write to a temporary file, flush, then replace.
    /// </remarks>
    public bool AtomicReplace { get; init; } = true;

    /// <summary>How many closed entries are kept before the oldest are pruned.</summary>
    public int RetainedClosedEntries { get; init; } = 200;

    /// <summary>
    /// What to do with a journal file that cannot be parsed.
    /// </summary>
    /// <remarks>
    /// Quarantined rather than deleted. A corrupt journal is the record of hardware that may still be
    /// in a changed state; deleting it destroys the only evidence a person could use, while trusting
    /// it could drive a restore from garbage.
    /// </remarks>
    public CorruptionResponse OnCorruption { get; init; } = CorruptionResponse.QuarantineFile;

    /// <summary>The default policy.</summary>
    public static JournalPolicy Default { get; } = new();
}

/// <summary>What to do with an unreadable journal.</summary>
public enum CorruptionResponse
{
    /// <summary>Move it aside, report it, and start a new one. Never delete it.</summary>
    QuarantineFile,

    /// <summary>Refuse to activate until a person resolves it.</summary>
    BlockActivation,
}
