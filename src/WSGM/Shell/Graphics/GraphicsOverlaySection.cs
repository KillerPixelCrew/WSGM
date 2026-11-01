using System.Collections.Generic;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>One section a graphics publisher declared: an adapter or a display.</summary>
/// <param name="Key">
///     The route key, <c>&lt;pluginId&gt;/&lt;sectionId&gt;</c>, or <c>&lt;pluginId&gt;/</c> for the rows a
///     publisher placed in no declared section.
/// </param>
/// <param name="PluginId">The graphics plugin that declared it.</param>
/// <param name="Title">The rail entry and the page heading.</param>
/// <param name="Description">What the section holds.</param>
/// <param name="Icon">The declared icon.</param>
/// <param name="Categories">The declared categories, in order.</param>
/// <param name="Capabilities">
///     Its rows, in placement order, projected like the Device rows with the graphics plugin named in
///     <see cref="DeviceOverlayCapability.GpuPluginId" />.
/// </param>
internal sealed record GraphicsOverlaySection(
    string Key,
    string PluginId,
    string Title,
    string Description,
    SectionIcon Icon,
    IReadOnlyList<DeviceOverlayCategory> Categories,
    IReadOnlyList<DeviceOverlayCapability> Capabilities);
