using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>One graphics publisher as the Graphics pages name it.</summary>
/// <param name="PluginId">The graphics plugin's id.</param>
/// <param name="Name">Its package's display name.</param>
/// <param name="Health">Its last reported health.</param>
/// <param name="Note">What to say about that health, or null while it is ready.</param>
internal sealed record GraphicsOverlayPublisher(string PluginId, string Name, PluginHealth Health, string? Note);

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

/// <summary>Everything the Graphics destination and the Graphics page in Steam draw.</summary>
/// <param name="Publishers">The running graphics publishers.</param>
/// <param name="Sections">Their sections, publisher by publisher, each in declared order.</param>
internal sealed record GraphicsOverlaySnapshot(
    IReadOnlyList<GraphicsOverlayPublisher> Publishers,
    IReadOnlyList<GraphicsOverlaySection> Sections)
{
    /// <summary>No graphics publisher is running.</summary>
    internal static GraphicsOverlaySnapshot Empty { get; } = new([], []);

    /// <summary>Whether the Graphics surfaces are offered: at least one graphics publisher runs.</summary>
    internal bool Visible => Publishers.Count > 0;
}

/// <summary>The closed source the Graphics destination and the Graphics page in Steam read and write.</summary>
internal interface IGraphicsOverlaySource : IDisposable
{
    /// <summary>Raised when page state may have changed; coordinator notifications use the UI dispatcher.</summary>
    /// <remarks>A stale-row refusal raises this on the write caller's thread. UI subscribers must dispatch when needed.</remarks>
    event Action? Changed;

    /// <summary>The pages as they should be drawn now.</summary>
    /// <returns>The snapshot.</returns>
    GraphicsOverlaySnapshot Snapshot();

    /// <summary>Writes one graphics capability as the user.</summary>
    /// <param name="capability">The row the user changed, as last drawn.</param>
    /// <param name="value">The new value, or null to run an action.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The plugin's answer, or null when the row no longer matches what the plugin publishes.</returns>
    Task<CapabilityCommandResult?> WriteAsync(
        DeviceOverlayCapability capability,
        CapabilityValue? value,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a graphics setting to Global for the running game.</summary>
    /// <param name="overrideId">The id the row carried in <see cref="DeviceOverlayCapability.OverrideId" />.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether an override was removed.</returns>
    Task<bool> UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default);
}
