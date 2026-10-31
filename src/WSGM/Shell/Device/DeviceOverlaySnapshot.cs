using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>Complete bounded Device-surface snapshot produced from coordinator-owned state.</summary>
/// <remarks>
///     The direct rows are WSGM's own controls — AutoTDP moves the plugin's power limit rather than
///     being one, the controller target and glyph selection are WSGM settings, the profile rows are
///     stored configuration, and recovery is an action on the cycle itself. Each carries its stable
///     focus key as the <see cref="DescriptorRow.Id" />, and a null row is simply not shown.
/// </remarks>
/// <param name="Visible">Whether the surface currently exposes device content.</param>
/// <param name="Status">Aggregate display status.</param>
/// <param name="Detail">Plain explanation of lifecycle or availability state.</param>
/// <param name="GlyphSelection">Host-owned glyph selection row, or null when absent.</param>
/// <param name="Capabilities">Current semantic rows, including generation stamps for revalidation.</param>
/// <param name="AutoTdp">Host-owned AutoTDP row, or null when unavailable.</param>
/// <param name="Controller">Controller target row, or null when absent.</param>
/// <param name="Recovery">Explicit cycle recovery action, or null when no recovery is offered.</param>
/// <param name="GlyphPreview">Artwork and physical-input preview, or null when absent.</param>
/// <param name="AuthoredProfile">Authored fan-profile selector, or null when absent.</param>
/// <param name="LightingProfile">Authored color-profile selector with its application status.</param>
internal sealed record DeviceOverlaySnapshot(
    bool Visible,
    string Status,
    string Detail,
    DescriptorRow? GlyphSelection,
    IReadOnlyList<DeviceOverlayCapability> Capabilities,
    DescriptorRow? AutoTdp = null,
    DescriptorRow? Controller = null,
    DescriptorRow? Recovery = null,
    DeviceOverlayGlyphPreview? GlyphPreview = null,
    DescriptorRow? AuthoredProfile = null,
    DescriptorRow? LightingProfile = null)
{
    /// <summary>Explicit host-owned choices, keyed by stable row identity.</summary>
    public IReadOnlyDictionary<string, DeviceHostSelection> HostSelections { get; init; } =
        new Dictionary<string, DeviceHostSelection>();

    /// <summary>
    ///     The temperature the software fan curve follows, rounded to a degree, which places the curve
    ///     marker; null without a fresh reading.
    /// </summary>
    public int? FanTemperature { get; init; }

    /// <summary>Plugin-declared overlay sections in presentation order.</summary>
    public IReadOnlyList<DeviceOverlayPluginSection> PluginSections { get; init; } =
        CapabilityRowProjection.ProjectSections(DeviceSections.All);

    /// <summary>AutoTDP's frame-rate target slider, or null while AutoTDP is off.</summary>
    public DescriptorRow? AutoTdpTarget { get; init; }
}
