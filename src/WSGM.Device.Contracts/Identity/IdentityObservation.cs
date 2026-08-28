using System.Collections.Generic;

namespace WSGM.Device.Contracts.Identity;

/// <summary>
/// One identity predicate in a device definition: a signal, the values it accepts, and how strongly
/// the definition binds to it.
/// </summary>
public sealed record IdentityObservation
{
    /// <summary>The exact machine-readable fact this observation matches against.</summary>
    public required IdentitySignal Signal { get; init; }

    /// <summary>How a match or mismatch affects candidate selection.</summary>
    public required IdentityStrength Strength { get; init; }

    /// <summary>
    /// Accepted values. Comparison is ordinal and case-insensitive after trimming, so a vendor that
    /// changes casing between firmware revisions does not silently break detection.
    /// </summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>
    /// Ranking contribution when <see cref="Strength"/> is <see cref="IdentityStrength.Weighted"/>.
    /// Ignored for every other strength.
    /// </summary>
    public int Weight { get; init; }

    /// <summary>
    /// Identifier of the endpoint this observation applies to, for USB and HID signals.
    /// </summary>
    /// <remarks>
    /// A handheld exposes several endpoints — gamepad, MCU, sensor — and a report length or
    /// <c>bcdDevice</c> is meaningless without saying which one it describes.
    /// </remarks>
    public string? EndpointId { get; init; }
}
