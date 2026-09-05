using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Interop;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class DevicePageCaptureTests
{
    private sealed record Publication(CapabilityDescriptorSet Descriptors, CapabilityState[] States);

    [AvaloniaTheory]
    [InlineData("Device", 1280, 800)]
    [InlineData("Power", 1280, 800)]
    [InlineData("Power", 1920, 1200)]
    [InlineData("RGB", 1280, 800)]
    [InlineData("Info", 1280, 800)]
    [InlineData("Controller", 1280, 800)]
    public async Task CompleteClawPublication(string page, int width, int height)
    {
        var publication = JsonSerializer.Deserialize<Publication>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "claw-ui-publication.json")))!;
        var views = publication.Descriptors.Descriptors.Select(descriptor => new DeviceCapabilityView(descriptor,
            new CapabilityProjection
            {
                State = publication.States.Last(state => state.CapabilityId == descriptor.CapabilityId
                && state.InstanceId == descriptor.InstanceId)
            }, null)).ToArray();
        var sections = DeviceSections.IncludePredefined(publication.Descriptors.Sections);
        var ids = sections.Select(section => section.SectionId).ToHashSet();
        using SimulatedDeviceOverlaySource hostControls = new();
        using FakeDevice device = new()
        {
            SampleSource = hostControls,
            State = hostControls.Snapshot() with
            {
                Status = "MSI Claw 8 AI+",
                Detail = "Device integration active",
                Capabilities = views.Select(view => DeviceOverlayBridge.ToOverlayCapability(view, ids)).ToArray(),
                PluginSections = DeviceOverlayBridge.ProjectSections(sections),
                Recovery = null,
            }
        };
        await using PerformanceService performance = new(new SimulatedRtssAdapter(), (_, _) => Task.CompletedTask,
            new PerformancePolicy(new PerformanceValues(60, 2), []));
        using PerformanceOverlayBridge performanceBridge = new(performance);
        using UiFixture fixture = new();
        var presets = new DevicePowerPresets(() => views,
            (_, _, _, _, _, _) => throw new InvalidOperationException("Unexpected hardware write"),
            new WindowsPowerModes(new ModeApi()), () => true);
        PerformanceConfig config = new()
        {
            AcPowerPreset = new() { PluginId = "claw", PresetId = "balanced" },
            BatteryPowerPreset = new() { PluginId = "claw", PresetId = "super-battery" },
        };
        var assignments = new DevicePowerAssignments(presets, () => new(config, null, "claw", 7, true, true),
            (_, _, _) => throw new InvalidOperationException("Unexpected assignment save"));
        using DevicePowerPresetSelection selection = new(presets, false, assignments);
        await selection.RefreshAsync();
        using PowerSchemeSelection schemes = new(new PowerSchemes(new OverlayInteractionTests.FakePower()),
            _ => throw new InvalidOperationException("Unexpected power plan write"));
        await schemes.RefreshAsync();
        OverlayWindow window = fixture.Overlay(width, height);
        window.AttachDeviceBridge(device);
        window.AttachPerformanceSource(performanceBridge);
        window.AttachPowerSchemes(schemes);
        window.AttachPowerPresets(selection);
        await performance.RefreshAsync();
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        if (page != "Device")
        {
            UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
                .Single(card => card.IsEffectivelyVisible && card.Title == page));
        }
        Dispatcher.UIThread.RunJobs();
        if (page == "Power")
        {
            Assert.False(UiFixture.Named<Expander>(window, "DeviceWindowsPower").IsExpanded);
            var cards = window.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("device-group") && border.IsEffectivelyVisible).ToArray();
            Assert.True(cards.Length >= 6);
            Assert.All(cards, card => Assert.InRange(card.Bounds.Width, 400, width / 2));
        }
        string directory = Path.Combine(RepositoryRoot(), "TestResults", "ui", "claw-" + page.ToLowerInvariant()
            + (width == 1280 ? string.Empty : "-" + width));
        Directory.CreateDirectory(directory);
        Capture(window, Path.Combine(directory, "viewport.png"));
        ScrollViewer scroll = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
        if (page == "Power" && width == 1280)
        {
            var details = window.GetVisualDescendants().OfType<Expander>()
                .Single(expander => Equals(expander.Header, "Profile details and reset"));
            details.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<CardButton>()
                .Single(card => card.Title == "Detected application").Focus(Avalonia.Input.NavigationMethod.Directional);
            Dispatcher.UIThread.RunJobs();
            window.MouseWheel(new Avalonia.Point(1100, 450), new Avalonia.Vector(0, -6));
            Dispatcher.UIThread.RunJobs();
            double before = scroll.Offset.Y;
            Assert.True(before > 0);
            for (int update = 0; update < 3; update++)
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
        window.FocusManager?.Focus(null);
        foreach (var visual in window.GetVisualDescendants().OfType<Avalonia.Animation.Animatable>())
        { visual.Transitions = null; }
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WSGM.slnx")))
        { directory = directory.Parent; }
        return directory?.FullName ?? throw new DirectoryNotFoundException("WSGM root");
    }

    private sealed class ModeApi : IPowerModeApi
    {
        public Guid Read() => Guid.Empty;
        public void Set(Guid mode) => throw new InvalidOperationException("Unexpected Windows write");
    }
}
