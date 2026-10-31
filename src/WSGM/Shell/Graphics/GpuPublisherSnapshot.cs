using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>One graphics publisher's page.</summary>
/// <param name="Publisher">The publisher.</param>
/// <param name="Sections">The overlay sections its descriptor set declares.</param>
/// <param name="Capabilities">Its capabilities in router order.</param>
internal sealed record GpuPublisherSnapshot(
    GpuPublisherView Publisher,
    IReadOnlyList<CapabilitySection> Sections,
    IReadOnlyList<GpuCapabilityView> Capabilities);
