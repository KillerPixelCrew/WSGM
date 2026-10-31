using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>One Device section as the root page presents it.</summary>
/// <param name="Section">The section this entry opens.</param>
/// <param name="Page">The navigation page it pushes.</param>
/// <param name="Title">Heading shown on the card and the page.</param>
/// <param name="Count">How many capabilities the section currently holds.</param>
internal sealed record DeviceOverlaySectionEntry(
    DeviceOverlaySection Section,
    OverlayPage Page,
    string Title,
    int Count)
{
    /// <summary>The plugin-declared section this entry opens, or null for a WSGM-owned one.</summary>
    /// <remarks>
    ///     When set, <see cref="Section" /> is meaningless and <see cref="Page" /> is
    ///     <see cref="OverlayPage.DevicePluginSection" />.
    /// </remarks>
    public string? PluginSectionId { get; init; }

    /// <summary>The declared icon for a plugin section's card.</summary>
    public SectionIcon Icon { get; init; } = SectionIcon.None;
}
