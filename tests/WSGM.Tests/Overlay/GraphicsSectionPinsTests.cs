using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.Tests.Overlay;

/// <summary>
///     The Quick Access pin identity of the Graphics groups: stable ids that name the plugin, the section and
///     the category, resolved against the current snapshot only.
/// </summary>
public sealed class GraphicsSectionPinsTests
{
    private const string Graphics = "section.graphics.wsgm.gpu.intel/graphics";
    private const string Display = "section.graphics.wsgm.gpu.intel/display-1a2b3c4d";

    [Fact]
    public void EachCategoryIsAGroupNamedByPluginSectionAndCategory()
    {
        using SimulatedGraphicsOverlaySource source = new();

        var pins = GraphicsSectionPins.Build(source.Snapshot()).ToArray();

        Assert.Equal(
        [
            Graphics + ".category.frame", Graphics + ".category.quality", Graphics + ".category.driver",
            Display + ".category.refresh", Display + ".category.picture", Display + ".category.color"
        ], pins.Select(pin => pin.Id));
        var frame = pins[0];
        Assert.Equal("Frame delivery", frame.Title);
        Assert.Equal("Graphics: Frame delivery", frame.PinTitle);
        Assert.Equal(["graphics.low-latency", "graphics.frame-rate-limit"],
            frame.Rows.Select(row => row.CapabilityId));
    }

    [Fact]
    public void RowsInNoDeclaredCategoryTakeTheSectionIdAndTitle()
    {
        using SimulatedGraphicsOverlaySource source = new();
        var section = source.Snapshot().Sections[0];
        var rows = section.Capabilities.ToArray();
        rows[0] = rows[0] with { CategoryId = null };
        rows[1] = rows[1] with { CategoryId = "undeclared" };

        var pins = GraphicsSectionPins.Groups(section with { Capabilities = rows }).ToArray();

        var lead = pins[0];
        Assert.Equal(Graphics, lead.Id);
        Assert.Equal("Graphics", lead.Title);
        Assert.Equal("Graphics", lead.PinTitle);
        Assert.Equal(["graphics.low-latency", "graphics.frame-rate-limit"], lead.Rows.Select(row => row.CapabilityId));
        Assert.DoesNotContain(pins, pin => pin.Id == Graphics + ".category.frame");
    }

    [Fact]
    public async Task IdsSurviveValueChanges()
    {
        using SimulatedGraphicsOverlaySource source = new();
        var before = GraphicsSectionPins.Build(source.Snapshot()).Select(pin => pin.Id).ToArray();
        var lowLatency = source.Snapshot().Sections.SelectMany(section => section.Capabilities)
            .Single(row => row.CapabilityId == "graphics.low-latency");

        Assert.True(await source.UseGlobalAsync(lowLatency.OverrideId!));

        Assert.Equal(before, GraphicsSectionPins.Build(source.Snapshot()).Select(pin => pin.Id));
    }

    [Fact]
    public void ResolveFindsOnlyAGroupTheCurrentSnapshotPublishes()
    {
        using SimulatedGraphicsOverlaySource source = new();
        var snapshot = source.Snapshot();

        var refresh = GraphicsSectionPins.Resolve(snapshot, Display + ".category.refresh");
        Assert.NotNull(refresh);
        Assert.Equal("wsgm.gpu.intel", refresh.Section.PluginId);
        Assert.All(refresh.Rows, row => Assert.Equal("refresh", row.CategoryId));

        // An absent publisher, section or category leaves the pin unresolved rather than removed.
        Assert.Null(GraphicsSectionPins.Resolve(null, Display + ".category.refresh"));
        Assert.Null(GraphicsSectionPins.Resolve(GraphicsOverlaySnapshot.Empty, Display + ".category.refresh"));
        Assert.Null(GraphicsSectionPins.Resolve(snapshot, "section.graphics.wsgm.gpu.other/graphics.category.frame"));
        Assert.Null(GraphicsSectionPins.Resolve(snapshot, Graphics + ".category.missing"));
        Assert.Null(GraphicsSectionPins.Resolve(snapshot, "section.device.plugin.graphics.main"));
    }

    [Fact]
    public void GraphicsIdsNeverCollideWithDeviceIds()
    {
        using SimulatedGraphicsOverlaySource source = new();

        Assert.All(GraphicsSectionPins.Build(source.Snapshot()), pin =>
        {
            Assert.StartsWith(GraphicsSectionPins.Prefix, pin.Id, StringComparison.Ordinal);
            Assert.False(pin.Id.StartsWith("section.device.", StringComparison.Ordinal));
        });
    }
}
