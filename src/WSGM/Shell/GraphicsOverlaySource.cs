using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
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

/// <summary>The wording both Graphics surfaces use for a graphics row.</summary>
internal static class GraphicsCapabilityText
{
    /// <summary>The vendor dropdown title in Quick Access.</summary>
    /// <param name="name">Publisher display name.</param>
    /// <returns>The name with a trailing Graphics replaced by GPU, an existing GPU suffix retained, or GPU appended.</returns>
    internal static string PublisherTitle(string name)
    {
        const string graphicsSuffix = " Graphics";
        return name.EndsWith(graphicsSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^graphicsSuffix.Length] + " GPU"
            : name.EndsWith(" GPU", StringComparison.OrdinalIgnoreCase)
                ? name
                : name + " GPU";
    }

    /// <summary>What a row says about when its value takes effect, or null when it does at once.</summary>
    /// <param name="timing">The descriptor's apply timing.</param>
    /// <returns>The note.</returns>
    internal static string? TimingNote(CapabilityApplyTiming timing)
    {
        return timing switch
        {
            CapabilityApplyTiming.NextApplicationStart => "Applies when a game next starts",
            CapabilityApplyTiming.SystemRestart => "Applies after restart",
            _ => null
        };
    }

    /// <summary>What a publisher's page says about its health, or null while it is ready.</summary>
    /// <param name="name">The publisher's name.</param>
    /// <param name="health">Its health.</param>
    /// <param name="detail">Its own detail, or null.</param>
    /// <returns>The note.</returns>
    internal static string? HealthNote(string name, PluginHealth health, string? detail)
    {
        return health switch
        {
            PluginHealth.Ready => null,
            PluginHealth.Failed => $"{name} stopped working. {detail}".TrimEnd(),
            _ => $"{name} is waiting for its driver. {detail}".TrimEnd()
        };
    }
}

/// <summary>Adapts the graphics coordinator to the Graphics destination and the Graphics page in Steam.</summary>
/// <remarks>
///     One projection for both surfaces, and the same row projection the Device destination uses, so a
///     graphics row reads, writes, marks a game override and reports why it is unavailable exactly as a
///     device row does. Only what is particular to graphics is added here: the rows' apply timing, and
///     that a Global-only row never shows a game override.
/// </remarks>
internal sealed class GraphicsOverlayBridge : IGraphicsOverlaySource
{
    private readonly GpuCoordinator _gpu;
    private bool _disposed;

    /// <summary>Subscribes to a borrowed graphics coordinator for shared page projections.</summary>
    /// <param name="gpu">The graphics coordinator, borrowed.</param>
    internal GraphicsOverlayBridge(GpuCoordinator gpu)
    {
        _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
        _gpu.Changed += OnGpuChanged;
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public GraphicsOverlaySnapshot Snapshot()
    {
        return Project(
        [
            .. _gpu.Publishers()
                .Select(publisher => _gpu.Snapshot(publisher.Identity.PluginId))
                .OfType<GpuPublisherSnapshot>()
        ]);
    }

    /// <inheritdoc />
    public async Task<CapabilityCommandResult?> WriteAsync(
        DeviceOverlayCapability capability,
        CapabilityValue? value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (capability.GpuPluginId is not { } pluginId)
        {
            return null;
        }

        var current = _gpu.Snapshot(pluginId)?.Capabilities.FirstOrDefault(candidate =>
            candidate.View.Descriptor.CapabilityId == capability.CapabilityId
            && candidate.View.Descriptor.InstanceId == capability.InstanceId)?.View;
        if (current is null)
        {
            // A deferred editor callback belongs to the descriptor the user actually saw.
            Changed?.Invoke();
            return null;
        }

        return await _gpu.ExecuteAsync(pluginId, capability.CapabilityId, capability.InstanceId, value,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default)
    {
        return _gpu.UseGlobalAsync(overrideId, cancellationToken);
    }

    /// <summary>Unsubscribes from the coordinator and releases subscribers; does not dispose the borrowed coordinator.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gpu.Changed -= OnGpuChanged;
        Changed = null;
    }

    /// <summary>Projects every publisher's page into the sections both Graphics surfaces draw.</summary>
    /// <param name="publishers">Each running publisher's snapshot, in the order they started.</param>
    /// <returns>The snapshot.</returns>
    internal static GraphicsOverlaySnapshot Project(IReadOnlyList<GpuPublisherSnapshot> publishers)
    {
        ArgumentNullException.ThrowIfNull(publishers);
        List<GraphicsOverlayPublisher> projected = [];
        List<GraphicsOverlaySection> sections = [];
        // Two graphics packages at once is unusual, but when it happens their sections say whose they are.
        var several = publishers.Count > 1;
        foreach (var publisher in publishers)
        {
            var pluginId = publisher.Publisher.Identity.PluginId;
            var name = publisher.Publisher.Name;
            projected.Add(new GraphicsOverlayPublisher(pluginId, name, publisher.Publisher.Health,
                GraphicsCapabilityText.HealthNote(name, publisher.Publisher.Health,
                    publisher.Publisher.HealthDetail)));
            HashSet<string> declared = new(publisher.Sections.Select(section => section.SectionId),
                StringComparer.Ordinal);
            var rows = publisher.Capabilities
                .Select((capability, index) => (Row: ProjectCapability(pluginId, capability, declared), Index: index))
                .OrderBy(item => item.Row.SortOrder)
                .ThenBy(item => item.Index)
                .Select(item => item.Row)
                .ToArray();
            foreach (var section in publisher.Sections
                         .Select((section, index) => (Section: section, Index: index))
                         .OrderBy(item => item.Section.SortOrder)
                         .ThenBy(item => item.Index)
                         .Select(item => item.Section))
            {
                var capabilities = rows.Where(row => row.PluginSectionId == section.SectionId).ToArray();
                if (capabilities.Length == 0)
                {
                    continue;
                }

                var title = Title(section.Key, section.CustomTitle, section.SectionId);
                sections.Add(new GraphicsOverlaySection(
                    pluginId + "/" + section.SectionId,
                    pluginId,
                    several ? $"{title} ({name})" : title,
                    section.CustomDescription ?? name,
                    section.Icon,
                    [
                        .. section.Categories
                            .Select((category, index) => (Category: category, Index: index))
                            .OrderBy(item => item.Category.SortOrder)
                            .ThenBy(item => item.Index)
                            .Select(item => new DeviceOverlayCategory(item.Category.CategoryId,
                                Title(item.Category.Key, item.Category.CustomTitle, item.Category.CategoryId)))
                    ],
                    capabilities));
            }

            var unplaced = rows.Where(row => row.PluginSectionId is null).ToArray();
            if (unplaced.Length > 0)
            {
                sections.Add(new GraphicsOverlaySection(pluginId + "/", pluginId, several ? $"{name}: other" : "Other",
                    name, SectionIcon.None, [], unplaced));
            }
        }

        return new GraphicsOverlaySnapshot(projected, sections);
    }

    /// <summary>One graphics row: the Device projection, with graphics' own scope and timing.</summary>
    /// <param name="pluginId">The graphics plugin that publishes it.</param>
    /// <param name="capability">The coordinator's view of it.</param>
    /// <param name="declared">The sections the publisher declared.</param>
    /// <returns>The row.</returns>
    internal static DeviceOverlayCapability ProjectCapability(
        string pluginId,
        GpuCapabilityView capability,
        IReadOnlySet<string> declared)
    {
        var view = capability.View;
        var projection = view.Projection;
        var row = DeviceOverlayBridge.ToOverlayCapability(view, declared);
        // A native per-application value is saved for the running game rather than written, and its driver
        // applies it when the game starts, so the row shows the game's value rather than the Global one the
        // driver reports.
        var current = projection is
        {
            ProfileScope: CapabilityProfileScope.NativePerApplication,
            DesiredSource: ProfileSource.Game,
            DesiredValue: { } game,
            PendingValue: null
        }
            ? game
            : row.CurrentValue;
        var timing = GraphicsCapabilityText.TimingNote(projection.ApplyTiming);
        var normal = projection.Progress is CommandProgress.Idle or CommandProgress.Completed
                     && !projection.DesiredValueOutOfRange
                     && projection.State.Reason is null;
        // The normal line is the Device page's quality and persistence, which says nothing on a graphics
        // row; the timing is what the user needs to know there. Anything unusual keeps its own line.
        var description = normal
            ? timing ?? string.Empty
            : timing is null
                ? row.Description
                : $"{row.Description} · {timing}";
        return row with
        {
            GpuPluginId = pluginId,
            CurrentValue = current,
            Description = description,
            // Global-only values are saved to Global whatever runs, so a game never overrides them.
            OverrideId = projection.ProfileScope is CapabilityProfileScope.GlobalOnly ? null : capability.OverrideId
        };
    }

    private static string Title(SettingSectionKey key, string? custom, string fallback)
    {
        return key is SettingSectionKey.Custom ? custom ?? fallback : key.ToString();
    }

    // Raised on the UI dispatcher by the graphics coordinator.
    private void OnGpuChanged()
    {
        Changed?.Invoke();
    }
}
