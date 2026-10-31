using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Overlay;

/// <summary>One Device page as the section rail presents it.</summary>
/// <param name="SectionId">The published section the entry opens.</param>
/// <param name="Title">Heading shown on the rail and the page.</param>
/// <param name="Icon">The section's declared icon.</param>
/// <param name="Count">How many rows the page draws.</param>
internal sealed record DeviceOverlaySectionEntry(string SectionId, string Title, SectionIcon Icon, int Count);
