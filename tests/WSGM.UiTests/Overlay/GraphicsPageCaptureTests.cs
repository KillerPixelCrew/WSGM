using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.Testing;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;
using Path = Avalonia.Controls.Shapes.Path;

namespace WSGM.UiTests.Overlay;

/// <summary>
///     The Device GPU section and a pinned Graphics group on Quick Access, drawn from the Intel graphics
///     package's publication fixture.
/// </summary>
public sealed class GraphicsPageCaptureTests
{
    private const string FramesPin = GraphicsSectionPins.Prefix + FixtureGraphicsSource.AdapterSection
                                                                + ".category.frames";

    private const string RefreshPin = GraphicsSectionPins.Prefix + FixtureGraphicsSource.DisplaySection
                                                                 + ".category.refresh";

    [AvaloniaTheory]
    [InlineData("overlay-graphics-adapter-1280", "adapter", 1280, 800)]
    [InlineData("overlay-graphics-adapter-1920", "adapter", 1920, 1080)]
    [InlineData("overlay-graphics-display-1280", "display", 1280, 800)]
    [InlineData("overlay-graphics-display-1920", "display", 1920, 1080)]
    [InlineData("overlay-graphics-pinned-1280", "pinned", 1280, 800)]
    public void IntelPublication(string name, string page, int width, int height)
    {
        using var graphics = FixtureGraphicsSource.Load();
        using UiFixture fixture = new();
        var window = fixture.Overlay(width, height);
        window.AttachGraphicsSource(graphics);
        Dispatcher.UIThread.RunJobs();
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        Assert.Equal("Device", UiFixture.Named<TextBlock>(window, "WorkspaceTitle").Text);
        UiFixture.Click(window, UiFixture.Rail(window, "device.gpu"));
        Assert.Contains("selected", UiFixture.Rail(window, "device.gpu").Classes);
        UiFixture.OpenSections(window, UiFixture.Named<StackPanel>(window, "GraphicsCapabilityList"));
        if (page == "pinned")
        {
            List<string> pins = [];
            window.PinToggleRequested += id =>
            {
                pins.Add(id);
                window.SetPins([.. pins]);
            };
            PinGroup(window, FramesPin);
            PinGroup(window, RefreshPin);
            Assert.Equal([FramesPin, RefreshPin], pins);
            UiFixture.Click(window, UiFixture.Tab(window, 0));
            var pinned = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
            UiFixture.OpenSections(window, pinned);
            Assert.Equal(2, pinned.Children.Count);
            Assert.Equal(
            [
                "graphics.frame-limit", "graphics.frame-sync", "graphics.vrr-windowed", "graphics.low-latency",
                "display.variable-refresh", "display.arc-sync-profile", "display.arc-sync-min-refresh",
                "display.arc-sync-max-refresh", "display.arc-sync-frame-time-increase",
                "display.arc-sync-frame-time-decrease"
            ], pinned.GetVisualDescendants().OfType<DeviceCapabilityControl>().Select(row => row.CapabilityId));
            Assert.Contains(pinned.GetVisualDescendants().OfType<SectionPinHeader>(),
                header => header.SectionId == RefreshPin);
            UiFixture.Named<Control>(window, "PinToast").IsVisible = false;
        }
        else
        {
            var key = page == "display" ? FixtureGraphicsSource.DisplaySection : FixtureGraphicsSource.AdapterSection;
            var section = graphics.Snapshot().Sections.Single(candidate => candidate.Key == key);
            var list = UiFixture.Named<StackPanel>(window, "GraphicsCapabilityList");
            UiFixture.OpenSections(window, list);
            // Groups fill the shorter column first, so the visual order is not the declared one.
            Assert.Equal(
                graphics.Snapshot().Sections.SelectMany(candidate => candidate.Capabilities)
                    .Select(row => row.CapabilityId).Order(),
                list.GetVisualDescendants().OfType<DeviceCapabilityControl>().Select(row => row.CapabilityId)
                    .Order());
            Assert.Equal(
                graphics.Snapshot().Sections.SelectMany(GraphicsSectionPins.Groups).Select(group => group.Id).Order(),
                list.GetVisualDescendants().OfType<SectionPinHeader>().Select(header => header.SectionId).Order());
            if (page == "adapter")
            {
                // The running game's Low latency, a value applying at the next game start and a Global-only
                // value applying after restart.
                Assert.NotNull(section.Capabilities.Single(row => row.CapabilityId == "graphics.low-latency")
                    .OverrideId);
                Assert.Null(section.Capabilities.Single(row => row.CapabilityId == "graphics.shared-memory")
                    .OverrideId);
                Assert.Contains(VisibleText(list), text => text == "Applies when a game next starts");
                Assert.Contains(VisibleText(list), text => text == "Applies after restart");
                // A live status row is a reading: never written, never overridden per game.
                var live = section.Capabilities.Single(row => row.CapabilityId == "graphics.live-api");
                Assert.False(live.Writable);
                Assert.Null(live.OverrideId);
                Assert.Single(Row(list, "graphics.live-api").GetVisualDescendants().OfType<DeviceStatisticRow>());
            }
            else
            {
                // The Custom Arc Sync profile is in use with variable refresh on, so its four values are live
                // sliders.
                foreach (var id in new[]
                         {
                             "display.arc-sync-min-refresh", "display.arc-sync-max-refresh",
                             "display.arc-sync-frame-time-increase", "display.arc-sync-frame-time-decrease"
                         })
                {
                    Assert.True(section.Capabilities.Single(row => row.CapabilityId == id).CanInvoke, id);
                    var slider = Row(list, id).GetVisualDescendants().OfType<Slider>().Single();
                    Assert.True(slider.IsEffectivelyEnabled, id);
                }
            }
        }

        if (page == "display")
        {
            var list = UiFixture.Named<StackPanel>(window, "GraphicsCapabilityList");
            var header = list.GetVisualDescendants().OfType<SectionPinHeader>()
                .Single(candidate => candidate.SectionId == RefreshPin);
            var group = header.GetVisualAncestors().OfType<CollapsibleSection>().First();
            Assert.True(group.IsExpanded);
            var scroll = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
            var viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>()
                .Single(presenter => ReferenceEquals(presenter.TemplatedParent, scroll));
            // BringIntoView on the pin only reveals the heading, leaving its editors below the fold.
            // Align the expanded group with the viewport before capturing its display controls.
            scroll.Offset = new Vector(scroll.Offset.X,
                scroll.Offset.Y + group.TranslatePoint(default, viewport)!.Value.Y);
            Dispatcher.UIThread.RunJobs();
            AssertIntersectsViewport(group.Heading, viewport, RefreshPin);
            foreach (var id in new[]
                     {
                         "display.variable-refresh", "display.arc-sync-profile", "display.arc-sync-min-refresh",
                         "display.arc-sync-max-refresh", "display.arc-sync-frame-time-increase",
                         "display.arc-sync-frame-time-decrease"
                     })
            {
                var control = Assert.Single(Row(group, id).GetVisualDescendants().OfType<Control>(),
                    candidate => candidate is ToggleSwitch or ComboBox or Slider);
                AssertIntersectsViewport(control, viewport, id);
            }
        }

        Dispatcher.UIThread.RunJobs();
        AssertRestingGraphics(page == "pinned"
            ? UiFixture.Named<Panel>(window, "PinnedSectionsGrid")
            : UiFixture.Named<StackPanel>(window, "GraphicsCapabilityList"), graphics);
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(height, window.ClientSize.Height);
        Rect? rasterNoiseRegion = null;
        if (page != "pinned")
        {
            var overviewIcon = UiFixture.Rail(window, "device.overview").GetVisualDescendants()
                .OfType<Path>().Single();
            rasterNoiseRegion = new Rect(overviewIcon.TranslatePoint(default, window)!.Value,
                overviewIcon.Bounds.Size);
        }

        VisualBaseline.Verify(window, name, rasterNoiseRegion);
        if (page != "pinned")
        {
            // The whole page for review, beside the baselined viewport; not a regression reference.
            var scroll = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
            window.Height += Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            Dispatcher.UIThread.RunJobs();
            var directory = System.IO.Path.Combine(RepositoryFiles.Root, "TestResults", "ui",
                $"graphics-{page}-{width}x{height}");
            Directory.CreateDirectory(directory);
            DevicePageCaptureTests.Capture(window, System.IO.Path.Combine(directory, "full.png"));
        }
    }

    private static void AssertRestingGraphics(Control root, FixtureGraphicsSource graphics)
    {
        var display = graphics.Snapshot().Sections
            .Single(section => section.Key == FixtureGraphicsSource.DisplaySection);
        Assert.True(display.Capabilities.Single(row => row.CapabilityId == "display.variable-refresh")
            .CurrentValue?.BooleanValue);
        Assert.Equal("custom", display.Capabilities.Single(row => row.CapabilityId == "display.arc-sync-profile")
            .CurrentValue?.ChoiceValue);
        foreach (var id in new[] { "display.variable-refresh", "display.arc-sync-profile" })
        {
            Assert.True(display.Capabilities.Single(row => row.CapabilityId == id).CanInvoke, id);
            var editor = Assert.Single(Row(root, id).GetVisualDescendants().OfType<Control>(),
                control => control is ToggleSwitch or ComboBox);
            Assert.True(editor.IsEffectivelyEnabled, id);
        }

        // Native Expander animations fade a body through its template ancestors even while its
        // editors are enabled. The capture must show the resting state configured by TestApplication.
        foreach (var section in root.GetVisualDescendants().OfType<CollapsibleSection>()
                     .Where(section => section.IsEffectivelyVisible && section.IsExpanded))
        {
            foreach (var visual in section.Body.GetVisualAncestors().Prepend(section.Body)
                         .TakeWhile(visual => !ReferenceEquals(visual, section)))
            {
                Assert.True(visual.Opacity == 1,
                    $"{section.Heading.Tag}: {visual.GetType().Name} must be fully opaque before capture.");
            }
        }
    }

    private static void AssertIntersectsViewport(Control control, Control viewport, string id)
    {
        Assert.True(control.IsEffectivelyVisible, id);
        var origin = control.TranslatePoint(default, viewport)!.Value;
        var bounds = new Rect(origin, control.Bounds.Size);
        var visible = bounds.Intersect(new Rect(viewport.Bounds.Size));
        Assert.True(visible.Width > 0 && visible.Height > 0,
            $"{id} must intersect the capture viewport: control {bounds}, viewport {viewport.Bounds.Size}.");
    }

    private static DeviceCapabilityControl Row(Control root, string capabilityId)
    {
        return root.GetVisualDescendants().OfType<DeviceCapabilityControl>()
            .Single(row => row.CapabilityId == capabilityId);
    }

    private static void PinGroup(OverlayWindow window, string id)
    {
        var header = window.GetVisualDescendants().OfType<SectionPinHeader>()
            .Single(header => header.IsEffectivelyVisible && header.SectionId == id);
        UiFixture.Click(window, header.GetVisualDescendants().OfType<Button>().Single());
    }

    private static IEnumerable<string?> VisibleText(Control root)
    {
        return root.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible)
            .Select(text => text.Text);
    }
}
