using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Overlay;
using WSGM.Testing;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

/// <summary>
///     The Graphics destination and a pinned Graphics group on Quick Access, drawn from the Intel graphics
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
        // With no device source attached the strip is Quick access, Steam, Graphics, Tools and Power.
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        Assert.Equal("Graphics", UiFixture.Named<TextBlock>(window, "WorkspaceTitle").Text);
        // The root passes straight through to the first section the package declares.
        Assert.Contains("selected", Rail(window, FixtureGraphicsSource.AdapterSection).Classes);
        if (page == "pinned")
        {
            List<string> pins = [];
            window.PinToggleRequested += id =>
            {
                pins.Add(id);
                window.SetPins([.. pins]);
            };
            PinGroup(window, FramesPin);
            UiFixture.Click(window, Rail(window, FixtureGraphicsSource.DisplaySection));
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
            if (page == "display")
            {
                UiFixture.Click(window, Rail(window, key));
            }

            var section = graphics.Snapshot().Sections.Single(candidate => candidate.Key == key);
            var list = UiFixture.Named<StackPanel>(window, "GraphicsCapabilityList");
            UiFixture.OpenSections(window, list);
            // Groups fill the shorter column first, so the visual order is not the declared one.
            Assert.Equal(section.Capabilities.Select(row => row.CapabilityId).Order(),
                list.GetVisualDescendants().OfType<DeviceCapabilityControl>().Select(row => row.CapabilityId)
                    .Order());
            Assert.Equal(GraphicsSectionPins.Groups(section).Select(group => group.Id).Order(),
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

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(height, window.ClientSize.Height);
        VisualBaseline.Verify(window, name);
        if (page != "pinned")
        {
            // The whole page for review, beside the baselined viewport; not a regression reference.
            var scroll = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
            window.Height += Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            Dispatcher.UIThread.RunJobs();
            var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui",
                $"graphics-{page}-{width}x{height}");
            Directory.CreateDirectory(directory);
            DevicePageCaptureTests.Capture(window, Path.Combine(directory, "full.png"));
        }
    }

    private static Button Rail(OverlayWindow window, string sectionKey)
    {
        return UiFixture.Rail(window, "graphics.section." + sectionKey);
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
