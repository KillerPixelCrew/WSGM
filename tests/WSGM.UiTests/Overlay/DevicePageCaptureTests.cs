using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Tests;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class DevicePageCaptureTests
{
    [AvaloniaTheory]
    [InlineData("Device", 1280, 800)]
    [InlineData("Power", 1280, 800)]
    [InlineData("Power", 1920, 1200)]
    [InlineData("RGB", 1280, 800)]
    [InlineData("Info", 1280, 800)]
    [InlineData("Controller", 1280, 800)]
    [InlineData("Pinned sections", 1280, 800)]
    public async Task CompleteClawPublication(string page, int width, int height)
    {
        var publication = JsonSerializer.Deserialize<Publication>(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "claw-ui-publication.json"),
            TestContext.Current.CancellationToken))!;
        var views = publication.Descriptors.Descriptors.Select(descriptor => new DeviceCapabilityView(descriptor,
            new CapabilityProjection
            {
                State = publication.States.Last(state => state.CapabilityId == descriptor.CapabilityId
                                                         && state.InstanceId == descriptor.InstanceId)
            }, null)).ToArray();
        var sections = DeviceSections.IncludePredefined(publication.Descriptors.Sections);
        var ids = sections.Select(section => section.SectionId).ToHashSet();
        using SimulatedDeviceOverlaySource hostControls = new();
        var state = hostControls.Snapshot() with
        {
            Status = "MSI Claw 8 AI+",
            Detail = "Device integration active",
            Capabilities = [.. views.Select(view => DeviceOverlayBridge.ToOverlayCapability(view, ids))],
            PluginSections = DeviceOverlayBridge.ProjectSections(sections),
            Recovery = null
        };
        using FakeDevice device = new();
        device.SampleSource = hostControls;
        device.State = state;
        await using PerformanceService performance = new(new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask,
            new PerformancePolicy(new PerformanceValues(60, 2), []));
        using PerformanceOverlayBridge performanceBridge = new(performance);
        using UiFixture fixture = new();
        var presets = new DevicePowerPresets(() => views,
            (_, _, _, _, _, _) => throw new InvalidOperationException("Unexpected hardware write"),
            new WindowsPowerModes(new ReadOnlyPowerModeApi()), () => true);
        PerformanceConfig config = new()
        {
            AcPowerPreset = new DevicePowerPresetReference { PluginId = "claw", PresetId = "balanced" },
            BatteryPowerPreset = new DevicePowerPresetReference { PluginId = "claw", PresetId = "super-battery" }
        };
        var assignments = new DevicePowerAssignments(presets,
            () => new DevicePowerAssignmentContext(config, null, "claw", 7, true, true),
            (_, _, _) => throw new InvalidOperationException("Unexpected assignment save"));
        using DevicePowerPresetSelection selection = new(presets, false, assignments);
        await selection.RefreshAsync();
        using PowerSchemeSelection schemes = new(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Unexpected power plan write"));
        await schemes.RefreshAsync();
        var window = fixture.Overlay(width, height);
        window.AttachDeviceBridge(device);
        window.AttachPerformanceSource(performanceBridge);
        window.AttachPowerSchemes(schemes);
        window.AttachPowerPresets(selection);
        await performance.RefreshAsync();
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        if (page == "Pinned sections")
        {
            List<string> pins = [];
            window.PinToggleRequested += id =>
            {
                pins.Add(id);
                window.SetPins([.. pins]);
            };
            UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
                .Single(card => card is { IsEffectivelyVisible: true, Title: "Power" }));
            foreach (var id in new[]
                     {
                         "section.device.plugin.power.category.control", "section.device.plugin.power.category.charging"
                     })
            {
                var header = window.GetVisualDescendants().OfType<SectionPinHeader>()
                    .Single(header => header.IsEffectivelyVisible && header.SectionId == id);
                UiFixture.Click(window, header.GetVisualDescendants().OfType<Button>().Single());
            }

            UiFixture.Click(window, UiFixture.Tab(window, 0));
            var sectionsPanel = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
            Assert.Equal(2, sectionsPanel.Children.Count);
            foreach (var capability in device.State.Capabilities.Where(capability =>
                         capability.CategoryId is "control" or "charging"))
            {
                var key = "pin:" + capability.CapabilityId +
                          (capability.InstanceId is { Length: > 0 } instance ? "#" + instance : "");
                Assert.Contains(sectionsPanel.GetVisualDescendants().OfType<Control>(),
                    control => Equals(control.Tag, key));
            }

            UiFixture.Named<Control>(window, "PinToast").IsVisible = false;
            VisualBaseline.Verify(window, "overlay-sections-1280");
        }
        else if (page != "Device")
        {
            UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
                .Single(card => card.IsEffectivelyVisible && card.Title == page));
        }

        Dispatcher.UIThread.RunJobs();
        if (page == "Power")
        {
            Assert.Equal(0, UiFixture.Named<ScrollViewer>(window, "ContentScroller").Offset.Y);
            Assert.True(UiFixture.Named<StackPanel>(window, "DeviceWindowsPower").IsEffectivelyVisible);
            var cards = window.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("device-group") && border.IsEffectivelyVisible).ToArray();
            Assert.True(cards.Length >= 5);
            Assert.All(cards, card => Assert.InRange(card.Bounds.Width, 400, width / 2.0));
        }

        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", "claw-" +
            page.ToLowerInvariant().Replace(' ', '-')
            + (width == 1280 ? string.Empty : "-" + width));
        Directory.CreateDirectory(directory);
        Capture(window, Path.Combine(directory, "viewport.png"));
        var scroll = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
        if (page == "Power" && width == 1280)
        {
            var details = window.GetVisualDescendants().OfType<Expander>()
                .Single(expander => Equals(expander.Header, "Profile details and reset"));
            details.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<CardButton>()
                .Single(card => card.Title == "Detected application").Focus(NavigationMethod.Directional);
            Dispatcher.UIThread.RunJobs();
            window.MouseWheel(new Point(1100, 450), new Vector(0, -6));
            Dispatcher.UIThread.RunJobs();
            var before = scroll.Offset.Y;
            Assert.True(before > 0);
            for (var update = 0; update < 3; update++)
            {
                device.Notify();
                await performanceBridge.SetValueAsync("frame-limit", 61 + update);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(before, scroll.Offset.Y, 1);
            }

            scroll.Offset = default;
            window.GetVisualDescendants().OfType<Expander>()
                .Single(expander => Equals(expander.Header, "Profile details and reset")).IsExpanded = false;
        }

        window.Height += Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(directory, "full.png"));
        Assert.True(views.Length >= 16);
        window.Close();
    }

    private static void Capture(Window window, string path)
    {
        window.FocusManager.Focus(null);
        foreach (var visual in window.GetVisualDescendants().OfType<Animatable>())
        {
            visual.Transitions = null;
        }

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path, new PngBitmapEncoderOptions());
    }

    private sealed record Publication(CapabilityDescriptorSet Descriptors, CapabilityState[] States);
}
