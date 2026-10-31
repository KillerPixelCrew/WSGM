using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Settings;
using WSGM.Plugin.Sdk;
using WSGM.Shell;
using static WSGM.Tests.Builders.CapabilityBuilders;

namespace WSGM.Tests.Shell;

/// <summary>
///     The one projection behind the overlay's Graphics destination and the Graphics page in Steam: a section
///     per adapter and display, rows as the Device rows are drawn, with graphics' own timing and scope.
/// </summary>
public sealed class GraphicsOverlayBridgeTests
{
    private const string Plugin = "wsgm.test-gpu";

    private static CapabilitySection Section(string id, string title, int order, params string[] categories)
    {
        return new CapabilitySection
        {
            SectionId = id,
            Key = SettingSectionKey.Custom,
            CustomTitle = title,
            SortOrder = order,
            Categories =
            [
                .. categories.Select((category, index) => new CapabilityCategory
                {
                    CategoryId = category, Key = SettingSectionKey.Custom, CustomTitle = category + " title",
                    SortOrder = index
                })
            ]
        };
    }

    private static CapabilityDescriptor Placed(CapabilityDescriptor descriptor, string section, string? category,
        CapabilityApplyTiming timing = CapabilityApplyTiming.Immediate)
    {
        return descriptor with { SectionId = section, CategoryId = category, ApplyTiming = timing };
    }

    private static GpuPublisherSnapshot Publisher(IReadOnlyList<CapabilitySection> sections,
        params GpuCapabilityView[] capabilities)
    {
        return new GpuPublisherSnapshot(
            new GpuPublisherView(GpuInstance, "Test graphics", GpuPublisher, PluginHealth.Ready, null),
            sections,
            capabilities);
    }

    [Fact]
    public void EverySectionBecomesAPageInDeclaredOrderKeyedByItsPublisher()
    {
        var snapshot = GraphicsOverlayBridge.Project(
        [
            Publisher(
                [Section("display", "Built-in display", 1, "refresh"), Section("graphics", "Graphics", 0, "frame")],
                new GpuCapabilityView(View(Placed(Toggle(CapabilityProfileScope.Switched, "a"), "display", "refresh"),
                    null, null), null),
                new GpuCapabilityView(View(Placed(Toggle(CapabilityProfileScope.Switched, "b"), "graphics", "frame"),
                    null, null), null))
        ]);

        Assert.True(snapshot.Visible);
        Assert.Equal(["wsgm.test-gpu/graphics", "wsgm.test-gpu/display"], snapshot.Sections.Select(s => s.Key));
        Assert.Equal(["Graphics", "Built-in display"], snapshot.Sections.Select(s => s.Title));
        var row = Assert.Single(snapshot.Sections[0].Capabilities);
        Assert.Equal("b", row.CapabilityId);
        Assert.Equal(Plugin, row.GpuPluginId);
        Assert.Equal("frame", row.CategoryId);
        Assert.Equal("frame title", Assert.Single(snapshot.Sections[0].Categories).Title);
    }

    [Fact]
    public void ASectionWithNoRowsIsDroppedAndUnplacedRowsGetTheirOwnPage()
    {
        var snapshot = GraphicsOverlayBridge.Project(
        [
            Publisher([Section("graphics", "Graphics", 0)],
                new GpuCapabilityView(View(Toggle(CapabilityProfileScope.Switched), null, null), null))
        ]);

        var section = Assert.Single(snapshot.Sections);
        Assert.Equal("wsgm.test-gpu/", section.Key);
        Assert.Null(Assert.Single(section.Capabilities).PluginSectionId);
    }

    [Fact]
    public void NoPublisherMeansNothingIsOffered()
    {
        Assert.False(GraphicsOverlayBridge.Project([]).Visible);
        Assert.False(GraphicsOverlaySnapshot.Empty.Visible);
    }

    [Fact]
    public void AGameOverrideCarriesItsIdForUseGlobal()
    {
        var row = GraphicsOverlayBridge.ProjectCapability(Plugin,
            new GpuCapabilityView(View(Toggle(CapabilityProfileScope.Switched), Flag(true), Flag(false),
                ProfileSource.Game), "gpu:wsgm.test-gpu:graphics.toggle"), new HashSet<string>());

        Assert.Equal("gpu:wsgm.test-gpu:graphics.toggle", row.OverrideId);
    }

    [Fact]
    public void AGlobalOnlyRowNeverShowsAGameOverride()
    {
        var row = GraphicsOverlayBridge.ProjectCapability(Plugin,
            new GpuCapabilityView(View(Toggle(CapabilityProfileScope.GlobalOnly), Flag(true), Flag(true),
                ProfileSource.Game), "gpu:wsgm.test-gpu:graphics.toggle"), new HashSet<string>());

        Assert.Null(row.OverrideId);
    }

    [Theory]
    [InlineData(CapabilityApplyTiming.Immediate, "")]
    [InlineData(CapabilityApplyTiming.NextApplicationStart, "Applies when a game next starts")]
    [InlineData(CapabilityApplyTiming.SystemRestart, "Applies after restart")]
    public void ARowSaysWhenItsValueTakesEffect(CapabilityApplyTiming timing, string expected)
    {
        var descriptor = Toggle(CapabilityProfileScope.Switched) with { ApplyTiming = timing };

        var row = GraphicsOverlayBridge.ProjectCapability(Plugin,
            new GpuCapabilityView(View(descriptor, null, null), null), new HashSet<string>());

        Assert.Equal(expected, row.Description);
    }

    [Fact]
    public void AnUnavailableRowKeepsItsReasonBesideItsTimingAndCannotBeChanged()
    {
        var view = View(Toggle(CapabilityProfileScope.Switched) with
        {
            ApplyTiming = CapabilityApplyTiming.SystemRestart
        }, null, null);
        view = view with
        {
            Projection = view.Projection with
            {
                State = view.Projection.State with
                {
                    Available = false,
                    Reason = new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing, "Turn on VRR first.")
                }
            }
        };

        var row = GraphicsOverlayBridge.ProjectCapability(Plugin, new GpuCapabilityView(view, null),
            new HashSet<string>());

        Assert.False(row.CanInvoke);
        Assert.Equal("Turn on VRR first. · Applies after restart", row.Description);
    }

    [Fact]
    public void ANativePerApplicationRowShowsTheRunningGamesValue()
    {
        // The driver reports Global's value; the game's own is what its driver applies at launch.
        var row = GraphicsOverlayBridge.ProjectCapability(Plugin,
            new GpuCapabilityView(View(Toggle(CapabilityProfileScope.NativePerApplication), Flag(true), Flag(false),
                ProfileSource.Game), "id"), new HashSet<string>());

        Assert.True(row.CurrentValue?.BooleanValue);
        Assert.Equal("id", row.OverrideId);
    }

    [Fact]
    public void ASwitchedRowShowsWhatTheDriverReports()
    {
        var row = GraphicsOverlayBridge.ProjectCapability(Plugin,
            new GpuCapabilityView(View(Toggle(CapabilityProfileScope.Switched), Flag(true), Flag(true),
                ProfileSource.Game), "id"), new HashSet<string>());

        Assert.False(row.CurrentValue?.BooleanValue);
    }

    [Fact]
    public void ANotReadyPublisherSaysSo()
    {
        var snapshot = GraphicsOverlayBridge.Project(
        [
            new GpuPublisherSnapshot(
                new GpuPublisherView(GpuInstance, "Test graphics", GpuPublisher, PluginHealth.Unavailable,
                    "No Intel adapter."),
                [],
                [])
        ]);

        var publisher = Assert.Single(snapshot.Publishers);
        Assert.Equal("Test graphics is waiting for its driver. No Intel adapter.", publisher.Note);
        Assert.Empty(snapshot.Sections);
        Assert.True(snapshot.Visible);
    }

    [Fact]
    public async Task TheSimulatedSourceShowsEveryKindOfGraphicsRow()
    {
        using SimulatedGraphicsOverlaySource source = new();
        var rows = source.Snapshot().Sections.SelectMany(section => section.Capabilities).ToArray();

        var lowLatency = rows.Single(row => row.CapabilityId == "graphics.low-latency");
        Assert.NotNull(lowLatency.OverrideId);
        Assert.Equal("Applies when a game next starts", lowLatency.Description);
        var memory = rows.Single(row => row.CapabilityId == "graphics.shared-memory");
        Assert.Null(memory.OverrideId);
        Assert.Equal("Applies after restart", memory.Description);
        Assert.False(rows.Single(row => row.CapabilityId == "display.arc-sync-profile").CanInvoke);

        Assert.True(await source.UseGlobalAsync(lowLatency.OverrideId!));
        Assert.Null(source.Snapshot().Sections.SelectMany(section => section.Capabilities)
            .Single(row => row.CapabilityId == "graphics.low-latency").OverrideId);
    }
}
