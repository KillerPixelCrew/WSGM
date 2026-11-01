using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>One named profile the user authored for a device capability.</summary>
/// <remarks>
///     A profile is not a setting. A setting is one value WSGM keeps and hands the plugin; a profile is
///     a named shape the user builds and then applies, globally or per application, from the overlay.
///     That is why curves are refused as settings and live here instead — one home each.
/// </remarks>
public sealed class DeviceAuthoredProfile
{
    /// <summary>Stable identifier the overlay selects by.</summary>
    /// <remarks>
    ///     Separate from <see cref="Name" /> so renaming a profile does not detach every application
    ///     override that pointed at it.
    /// </remarks>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>What the user called it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The capability this profile authors, for example a fan curve or a lighting colour.</summary>
    public string CapabilityId { get; set; } = string.Empty;

    /// <summary>Curve points, ascending by input. Empty for a profile that is not a curve.</summary>
    public List<AuthoredCurvePoint> Curve { get; set; } = [];

    /// <summary>Packed colour for a lighting profile, or null when this is not one.</summary>
    public int? Color { get; set; }
}
