using System.Text.Json;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;
using WSGM.Shell;

namespace WSGM.UiTests.Fakes;

/// <summary>
///     The Intel graphics package's publication from <c>Fixtures/intel-graphics-ui-publication.json</c>, as the
///     Graphics destination reads it.
/// </summary>
/// <remarks>
///     The rows run through the production projection. The running game's own profile sets Low latency, so
///     one row shows a game override with Use global. Every write fails the test.
/// </remarks>
internal sealed class FixtureGraphicsSource : IGraphicsOverlaySource
{
    internal const string PluginId = "wsgm.gpu.intel";
    internal const string AdapterSection = PluginId + "/graphics";
    internal const string DisplaySection = PluginId + "/display-1a2b3c4d";
    private const string GameOverride = "graphics.low-latency";

    private readonly Publication _publication;

    private FixtureGraphicsSource(Publication publication)
    {
        _publication = publication;
    }

    internal int Subscribers { get; private set; }

    public event Action? Changed
    {
        add { Subscribers++; }
        remove { Subscribers--; }
    }

    public GraphicsOverlaySnapshot Snapshot()
    {
        return GraphicsOverlayBridge.Project([PublisherSnapshot()]);
    }

    public Task<CapabilityCommandResult?> WriteAsync(DeviceOverlayCapability capability, CapabilityValue? value,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Unexpected graphics write");
    }

    public Task<bool> UseGlobalAsync(string overrideId, CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Unexpected graphics profile write");
    }

    public void Dispose()
    {
        Assert.Equal(0, Subscribers);
    }

    internal static FixtureGraphicsSource Load()
    {
        return new FixtureGraphicsSource(JsonSerializer.Deserialize<Publication>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "intel-graphics-ui-publication.json")))!);
    }

    /// <summary>
    ///     The variable-refresh row the Power and thermals section shows while the device package publishes
    ///     none, projected as <see cref="DeviceOverlayBridge" /> projects it.
    /// </summary>
    /// <param name="declaredSections">The device package's declared section ids.</param>
    /// <returns>The row.</returns>
    internal DeviceOverlayCapability VariableRefreshRow(IReadOnlySet<string> declaredSections)
    {
        var vrr = VariableRefreshCapabilities.Select(
        [
            .. PublisherSnapshot().Capabilities
                .Where(capability => capability.View.Descriptor.Role is CapabilityRole.VariableRefreshRate)
                .Select(capability => new PublishedCapability(capability.View, PluginId))
        ])!;
        return DeviceOverlayBridge.ToOverlayCapability(vrr.View, declaredSections) with
        {
            GpuPluginId = vrr.GpuPluginId,
            PluginSectionId = null,
            CategoryId = null
        };
    }

    private GpuPublisherSnapshot PublisherSnapshot()
    {
        var key = ProfileSettingKey.GpuPublisher(PluginId);
        GpuPublisherView publisher = new(new PluginInstanceIdentity(PluginId, "default"), "Intel Graphics", key,
            PluginHealth.Ready, null);
        return new GpuPublisherSnapshot(publisher, _publication.Descriptors.Sections,
        [
            .. _publication.Descriptors.Descriptors.Select(descriptor =>
            {
                var state = _publication.States.Last(state => state.CapabilityId == descriptor.CapabilityId
                                                              && state.InstanceId == descriptor.InstanceId);
                var game = descriptor.CapabilityId == GameOverride ? CapabilityValue.Choice("boost") : null;
                DeviceCapabilityView view = new(descriptor, new CapabilityProjection
                {
                    State = state,
                    DesiredValue = game ?? state.ObservedValue,
                    DesiredSource = game is null ? ProfileSource.Global : ProfileSource.Game,
                    GlobalDesiredValue = state.ObservedValue,
                    ProfileScope = descriptor.ProfileScope,
                    ApplyTiming = descriptor.ApplyTiming
                }, null) { Publisher = key };
                return new GpuCapabilityView(view, DeviceOverlayBridge.OverrideIdFor(view, null));
            })
        ]);
    }

    private sealed record Publication(CapabilityDescriptorSet Descriptors, CapabilityState[] States);
}
