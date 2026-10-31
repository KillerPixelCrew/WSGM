using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Lifecycle;
using WSGM.Install;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

/// <summary>
///     The Device destination's pages and the Quick Access groups pinned from them, as compared baselines.
/// </summary>
/// <remarks>
///     Most cases draw the MSI Claw publication from <c>Fixtures/claw-ui-publication.json</c>. The pages a Claw
///     does not reach, the shared pages of rows a plugin left unplaced, the colour editor and the simulated
///     Controller page, draw a <see cref="FakeDevice" /> snapshot. Every hardware write fails the test.
/// </remarks>
public sealed class DevicePageCaptureTests
{
    [AvaloniaTheory]
    [InlineData("Device", 1280, 800)]
    [InlineData("Device", 980, 640)]
    [InlineData("Power", 1280, 720)]
    [InlineData("Power", 980, 640)]
    [InlineData("Power", 3840, 2160)]
    [InlineData("Power", 1280, 800)]
    [InlineData("Power", 1920, 1200)]
    [InlineData("RGB", 1280, 800)]
    [InlineData("Info", 1280, 800)]
    [InlineData("Controller", 1280, 800)]
    [InlineData("Pinned sections", 1280, 800)]
    [InlineData("AutoTDP", 980, 640)]
    [InlineData("AutoTDP", 1280, 800)]
    public async Task CompleteClawPublication(string page, int width, int height)
    {
        await using var claw = await ClawOverlay.CreateAsync(width, height, page == "AutoTDP");
        var window = claw.Window;
        var device = claw.Device;
        if (page == "Pinned sections")
        {
            List<string> pins = [];
            window.PinToggleRequested += id =>
            {
                pins.Add(id);
                window.SetPins([.. pins]);
            };
            UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin.power"));
            UiFixture.OpenSections(window);
            PinGroups(window,
                "section.device.plugin.power.category.control", "section.device.plugin.power.category.charging");

            UiFixture.Click(window, UiFixture.Tab(window, 0));
            var sectionsPanel = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
            UiFixture.OpenSections(window, sectionsPanel);
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
            return;
        }

        if (page != "Device")
        {
            var section = page switch
            {
                "Power" or "AutoTDP" => "power", "RGB" => "rgb", "Info" => "info", _ => "controller"
            };
            UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin." + section));
        }

        Dispatcher.UIThread.RunJobs();
        if (page == "Device")
        {
            UiFixture.OpenSections(window);
            Dispatcher.UIThread.RunJobs();
            // The Overview carries the Power page's folds, assignments included, under the per-game switch.
            var folds = window.GetVisualDescendants().OfType<CollapsibleSection>()
                .Where(section => section.IsEffectivelyVisible).ToArray();
            var power = Assert.Single(folds, card => Equals(card.Heading.Tag, "section.device.unified-power.expand"));
            Assert.Single(folds, card => Equals(card.Heading.Tag, "section.device.plugin.power.category.control.expand"));
            Assert.True(UiFixture.Tagged<DeviceGroupView>(window, "section.device.power-presets").IsEffectivelyVisible);
            Assert.Contains(power.GetVisualDescendants().OfType<Control>(),
                control => Equals(control.Tag, "power.primary-limit"));
            var toggle = UiFixture.Tagged<DeviceCapabilityControl>(window, "device.application-profile");
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.True(toggle.TranslatePoint(default, window)!.Value.Y < power.TranslatePoint(default, window)!.Value.Y,
                "The per-application switch heads the Overview.");
        }

        if (page == "Power")
        {
            UiFixture.OpenSections(window);
            UiFixture.Named<ScrollViewer>(window, "ContentScroller").Offset = default;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, UiFixture.Named<ScrollViewer>(window, "ContentScroller").Offset.Y);
            Assert.True(UiFixture.Tagged<DeviceGroupView>(window, "section.system.power-profile").IsEffectivelyVisible);
            var cards = window.GetVisualDescendants().OfType<CollapsibleSection>()
                .Where(section => section.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(cards);
            var detail = UiFixture.Named<Control>(window, "DevicePage");
            Assert.All(cards, card => Assert.InRange(card.Bounds.Width, 240, detail.Bounds.Width));
            var power = Assert.Single(cards, card => Equals(card.Heading.Tag, "section.device.unified-power.expand"));
            Assert.DoesNotContain(cards, card => Equals(card.Heading.Tag, "section.system.power-profile.expand"));
            // The Claw's fan group is its own fold after Power, not one of Power's open groups.
            var fans = Assert.Single(cards, card => Equals(card.Heading.Tag,
                "section.device.plugin.power.category.control.expand"));
            Assert.DoesNotContain(fans, power.GetVisualDescendants());
            Assert.Contains(fans.GetVisualDescendants().OfType<Control>(), control => Equals(control.Tag, "fan.mode"));
            Assert.DoesNotContain(power.GetVisualDescendants().OfType<Control>(),
                control => Equals(control.Tag, "fan.mode"));
            Assert.True(fans.TranslatePoint(default, window)!.Value.Y
                        >= power.TranslatePoint(new Point(0, power.Bounds.Height), window)!.Value.Y,
                "Fans follows Power.");
            foreach (var header in cards.SelectMany(card => card.GetVisualDescendants()
                         .OfType<SectionPinHeader>()))
            {
                var fold = header.GetVisualAncestors().OfType<CollapsibleSection>().FirstOrDefault();
                var pin = Assert.Single(header.Children.OfType<Button>());
                if (fold is not null && !header.Children.OfType<TextBlock>().Any(text => text.IsVisible))
                {
                    var title = fold.GetVisualDescendants().OfType<TextBlock>()
                        .First(text => text.FontSize == 18);
                    var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), fold)!.Value.X;
                    var pinLeft = pin.TranslatePoint(default, fold)!.Value.X;
                    Assert.True(titleRight <= pinLeft, "The heading must not overlap its pin action.");
                }
                else
                {
                    var title = Assert.Single(header.Children.OfType<TextBlock>());
                    Assert.True(title.Bounds.Right < pin.Bounds.Left, "The heading must not overlap its pin action.");
                }

                Assert.InRange(pin.Bounds.Height, 36, header.Bounds.Height);
            }

            var planGroup = UiFixture.Tagged<DeviceGroupView>(window, "section.system.power-profile");
            Assert.Contains("device-group", planGroup.Classes);
            var assignmentsGroup = UiFixture.Tagged<DeviceGroupView>(window, "section.device.power-presets");
            Assert.Contains("device-group", assignmentsGroup.Classes);
            // AC and battery assignments lead; the Windows plan follows after a visible gap.
            Assert.True(planGroup.TranslatePoint(default, window)!.Value.Y
                - assignmentsGroup.TranslatePoint(new Point(0, assignmentsGroup.Bounds.Height), window)!.Value.Y >= 12,
                "Separate power sections need a visible gap.");
        }

        if (page == "AutoTDP")
        {
            UiFixture.OpenSections(window);
            Dispatcher.UIThread.RunJobs();
            var toggle = window.GetVisualDescendants().OfType<Control>().First(control =>
                control.IsEffectivelyVisible && Equals(control.Tag, DeviceHostRowIds.AutoTdp));
            var slider = Assert.Single(window.GetVisualDescendants().OfType<Slider>(), candidate =>
                candidate.IsEffectivelyVisible && Equals(candidate.Tag, DeviceHostRowIds.AutoTdpTarget));
            Assert.True(slider.IsEffectivelyEnabled);
            Assert.Equal(AutoTdpTarget.MinimumFps, slider.Minimum);
            Assert.Equal(AutoTdpTarget.MaximumFps, slider.Maximum);
            Assert.True(slider.TranslatePoint(default, window)!.Value.Y > toggle.TranslatePoint(default, window)!.Value.Y,
                "The target slider follows the AutoTDP switch.");
            var detail = UiFixture.Named<Control>(window, "DevicePage");
            Assert.True(slider.TranslatePoint(new Point(slider.Bounds.Width, 0), detail)!.Value.X <= detail.Bounds.Width,
                "The target slider fits the controls pane.");
            slider.BringIntoView();
            Dispatcher.UIThread.RunJobs();
        }

        if (page is "Device" or "RGB" or "Info" or "Controller")
        {
            // Open groups, from the top of the page. The pin choices have their own capture.
            UiFixture.OpenSections(window);
            ClosePinChoices(window);
            ScrollToTop(window);
        }

        // The viewport the page opens on, then the whole page grown to its content.
        var name = "claw-" + page.ToLowerInvariant().Replace(' ', '-') + $"-{width}x{height}";
        VisualBaseline.Verify(window, name);
        var scroll = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
        if (page == "Power" && width == 1280)
        {
            window.MouseWheel(new Point(1100, 450), new Vector(0, -6));
            Dispatcher.UIThread.RunJobs();
            var before = scroll.Offset.Y;
            Assert.True(before > 0);
            for (var update = 0; update < 3; update++)
            {
                device.Notify();
                await claw.PerformanceBridge.SetValueAsync("frame-limit", 61 + update);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(before, scroll.Offset.Y, 1);
            }

            scroll.Offset = default;
        }

        VerifyFullPage(window, name + "-full");
        Assert.True(claw.Views.Length >= 16);
    }

    /// <summary>The Overview with its Quick Access pins list open: every group the Device page offers to pin.</summary>
    [AvaloniaFact]
    public async Task OverviewPinChooser()
    {
        await using var claw = await ClawOverlay.CreateAsync(1280, 800);
        var window = claw.Window;
        var pinsFold = window.GetVisualDescendants().OfType<CollapsibleSection>()
            .Single(section => Equals(section.Heading.Tag, "device.quick-access-pins.expand"));
        foreach (var expander in pinsFold.GetVisualDescendants().OfType<Expander>())
        {
            expander.ContentTransition = null;
        }

        UiFixture.Click(window, pinsFold.Heading);
        Dispatcher.UIThread.RunJobs();
        Assert.True(pinsFold.IsExpanded);
        var offered = UiFixture.Named<DevicePageView>(window, "DevicePage").SectionPins.Children
            .OfType<SectionPinHeader>().Select(header => header.SectionId).ToArray();
        Assert.Contains("section.device.plugin.power.category.control", offered);
        ScrollToTop(window);
        VerifyFullPage(window, "claw-device-pins-1280x800-full");
    }

    /// <summary>The GPU page entered from the Overview, whose groups fold as on the GPU page entered from anywhere.</summary>
    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(980, 640)]
    public async Task GpuFromOverview(int width, int height)
    {
        await using var claw = await ClawOverlay.CreateAsync(width, height, gpu: ClawGpu.Published);
        var window = claw.Window;
        Assert.Contains("selected", UiFixture.Rail(window, "device.overview").Classes);
        UiFixture.Click(window, UiFixture.Rail(window, "device.gpu"));
        Dispatcher.UIThread.RunJobs();
        var list = UiFixture.Named<DevicePageView>(window, "DevicePage");
        // The GPU groups fold on their own, as on a GPU page entered from anywhere else.
        var groups = list.GetVisualDescendants().OfType<DeviceGroupView>()
            .Where(group => group.IsEffectivelyVisible && group.Group.Fold == DeviceFold.Folded).ToArray();
        Assert.NotEmpty(groups);
        Assert.All(groups, group => Assert.IsType<CollapsibleSection>(group.Child));
        VisualBaseline.Verify(window, $"claw-gpu-from-overview-{width}x{height}");
    }

    /// <summary>The GPU page without a graphics package, and with one that has published nothing yet.</summary>
    [AvaloniaTheory]
    [InlineData("unavailable")]
    [InlineData("empty")]
    public async Task GpuWithoutSettings(string state)
    {
        await using var claw = await ClawOverlay.CreateAsync(1280, 800,
            gpu: state == "empty" ? ClawGpu.Unpublished : ClawGpu.None);
        var window = claw.Window;
        UiFixture.Click(window, UiFixture.Rail(window, "device.gpu"));
        Dispatcher.UIThread.RunJobs();
        var list = UiFixture.Named<DevicePageView>(window, "DevicePage");
        var caption = Assert.Single(list.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text is
                "The graphics driver has not published any settings yet."
                or "GPU controls will appear here when a supported Intel, NVIDIA or AMD plugin is enabled.");
        Assert.Equal(state == "empty", caption.Text!.StartsWith("The graphics driver", StringComparison.Ordinal));
        VisualBaseline.Verify(window, $"claw-gpu-{state}-1280x800");
    }

    /// <summary>
    ///     Power's lead group, its fan group with the curve and its configuration group, pinned from the Power
    ///     page and drawn on Quick Access.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(980, 640)]
    public async Task PinnedPowerGroups(int width, int height)
    {
        await using var claw = await ClawOverlay.CreateAsync(width, height);
        var window = claw.Window;
        List<string> pins = [];
        window.PinToggleRequested += id =>
        {
            pins.Add(id);
            window.SetPins([.. pins]);
        };
        UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin.power"));
        UiFixture.OpenSections(window);
        string[] groups =
        [
            "section.device.plugin.power.main", "section.device.plugin.power.category.control",
            "section.device.plugin.power.configuration"
        ];
        PinGroups(window, groups);
        Assert.Equal(groups, pins);

        UiFixture.Click(window, UiFixture.Tab(window, 0));
        var pinned = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        UiFixture.OpenSections(window, pinned);
        Assert.Equal(3, pinned.Children.Count);
        Assert.Contains(pinned.GetVisualDescendants().OfType<Control>(), control => Equals(control.Tag, "pin:fan.curve"));
        Assert.Contains(pinned.GetVisualDescendants().OfType<Control>(),
            control => Equals(control.Tag, "pin:" + DeviceHostRowIds.AutoTdp));
        UiFixture.Named<ScrollViewer>(window, "ContentScroller").Offset = default;
        UiFixture.Named<Control>(window, "PinToast").IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        var name = $"claw-pinned-power-{width}x{height}";
        VisualBaseline.Verify(window, name);
        VerifyFullPage(window, name + "-full");
    }

    /// <summary>The Overview under the banner explaining a missing controller stack.</summary>
    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(980, 640)]
    public async Task PrerequisiteBanner(int width, int height)
    {
        await using var claw = await ClawOverlay.CreateAsync(width, height);
        var window = claw.Window;
        window.DevicePage.AttachPrerequisites(new DevicePrerequisiteSource(() =>
            new DevicePrerequisiteState(true, true, false, false, [SetupComponent.ControllerStack])));
        await window.DevicePage.PrerequisitesRefresh;
        UiFixture.OpenSections(window);
        ClosePinChoices(window);
        ScrollToTop(window);
        Assert.True(UiFixture.Named<DevicePageView>(window, "DevicePage").PrerequisiteBanner.IsEffectivelyVisible);
        VisualBaseline.Verify(window, $"claw-prerequisites-{width}x{height}");
    }

    /// <summary>The colour editor for a lighting ring, opened from its Choose colour action.</summary>
    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(980, 640)]
    public void ColorEditor(int width, int height)
    {
        using FakeDevice device = new();
        device.State = UndeclaredSections();
        using UiFixture fixture = new();
        var window = fixture.Overlay(width, height);
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin.rgb"));
        UiFixture.OpenSections(window);
        var row = window.GetVisualDescendants().OfType<DeviceCapabilityControl>()
            .Single(row => row.IsEffectivelyVisible && Equals(row.Tag, "lighting.zone-color#ring"));
        UiFixture.Click(window, row.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible));
        Dispatcher.UIThread.RunJobs();
        Assert.True(UiFixture.Named<DeviceColorView>(window, "DeviceColorHost").IsEffectivelyVisible);
        ScrollToTop(window);
        VisualBaseline.Verify(window, $"device-color-{width}x{height}");
    }

    /// <summary>
    ///     The shared Controller, RGB and Info pages holding the OEM buttons, lighting and readings a device
    ///     published without placing them, beside the recovery row.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(DeviceSections.ControllerId)]
    [InlineData(DeviceSections.RgbId)]
    [InlineData(DeviceSections.InfoId)]
    public void UnplacedRowsOnTheirSharedPage(string section)
    {
        using FakeDevice device = new();
        device.State = UndeclaredSections();
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin." + section));
        UiFixture.OpenSections(window);
        ScrollToTop(window);
        var list = UiFixture.Named<DevicePageView>(window, "DevicePage");
        Assert.Contains(list.GetVisualDescendants().OfType<Control>(),
            control => control is DeviceCapabilityControl or DeviceSettingRow);
        VisualBaseline.Verify(window, $"device-unplaced-{section}-1280x800");
    }

    /// <summary>A saved device pin whose group the device no longer publishes.</summary>
    [AvaloniaFact]
    public void PinnedUnavailableSection()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.SetPins(["section.device.plugin.cooling.category.control"]);
        Dispatcher.UIThread.RunJobs();
        var pinned = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        UiFixture.OpenSections(window, pinned);
        Assert.Contains(pinned.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text == "Section unavailable");
        UiFixture.Named<Control>(window, "PinToast").IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, "device-pinned-unavailable-1280x800");
    }

    /// <summary>
    ///     The shared Controller page of a device that declares no Controller section, then its glyph group pinned
    ///     from that page and drawn on Quick Access.
    /// </summary>
    [AvaloniaFact]
    public void SharedControllerPageAndPin()
    {
        using SimulatedDeviceOverlaySource simulated = new();
        using FakeDevice device = new();
        device.SampleSource = simulated;
        device.State = simulated.Snapshot();
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        List<string> pins = [];
        window.PinToggleRequested += id =>
        {
            pins.Add(id);
            window.SetPins([.. pins]);
        };
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin.controller"));
        UiFixture.OpenSections(window);
        ScrollToTop(window);
        VisualBaseline.Verify(window, "simulated-controller-1280x800");

        var header = window.GetVisualDescendants().OfType<SectionPinHeader>()
            .First(header => header.IsEffectivelyVisible);
        UiFixture.Click(window, header.GetVisualDescendants().OfType<Button>().Single());
        Assert.Single(pins);
        UiFixture.Click(window, UiFixture.Tab(window, 0));
        UiFixture.OpenSections(window, UiFixture.Named<Panel>(window, "PinnedSectionsGrid"));
        UiFixture.Named<Control>(window, "PinToast").IsVisible = false;
        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, "simulated-controller-pinned-1280x800");
    }

    internal static void Capture(Window window, string path)
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

    /// <summary>Folds the Overview's pin choices again, which have a capture of their own.</summary>
    private static void ClosePinChoices(Window window)
    {
        foreach (var pins in window.GetVisualDescendants().OfType<CollapsibleSection>()
                     .Where(section => Equals(section.Heading.Tag, "device.quick-access-pins.expand")))
        {
            pins.IsExpanded = false;
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Returns the controls pane to the top of its page.</summary>
    private static void ScrollToTop(Window window)
    {
        UiFixture.Named<ScrollViewer>(window, "ContentScroller").Offset = default;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Grows the window to the scroller's content and compares the whole page.</summary>
    private static void VerifyFullPage(Window window, string name)
    {
        var scroll = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
        window.Height += Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        Dispatcher.UIThread.RunJobs();
        VisualBaseline.Verify(window, name);
    }

    /// <summary>Presses the pin action of each named group on the page.</summary>
    private static void PinGroups(Window window, params string[] ids)
    {
        foreach (var id in ids)
        {
            var header = window.GetVisualDescendants().OfType<SectionPinHeader>()
                .Single(header => header.IsEffectivelyVisible && header.SectionId == id);
            UiFixture.Click(window, header.GetVisualDescendants().OfType<Button>().Single());
        }
    }

    /// <summary>
    ///     A device with OEM buttons, lighting and readings that places none of them in a section, so Shell puts
    ///     each on the shared page of its role.
    /// </summary>
    private static DeviceOverlaySnapshot UndeclaredSections()
    {
        return new DeviceOverlaySnapshot(true, "Fixture handheld", "Device integration active", null,
        [
            .. new[]
            {
                new DeviceOverlayCapability("fixture.temperature", null, DescriptorStatus.Available,
                    "Processor temperature", "Synthetic sensor", "45 °C", false),
                OemButton("oem.left", "Left OEM button", "overlay"),
                OemButton("oem.right", "Right OEM button", "keyboard"),
                new DeviceOverlayCapability("lighting.power", null, DescriptorStatus.Available, "Lighting",
                    string.Empty, "ON", true, CapabilityValue.Boolean(true), CapabilityValue.Boolean(false))
                {
                    Role = CapabilityRole.LightingPower, ValueKind = CapabilityValueKind.Boolean, Writable = true
                },
                new DeviceOverlayCapability("lighting.zone-color", "ring", DescriptorStatus.Available, "Ring",
                    "Opens the color editor", "#FF9D3D", true, CapabilityValue.Color(0xFF9D3D))
                {
                    Role = CapabilityRole.LightingZoneColor, ValueKind = CapabilityValueKind.Color, Writable = true
                },
                new DeviceOverlayCapability("firmware.version", null, DescriptorStatus.Available, "Firmware version",
                    "Reported by the controller", "1.0.7", false, CapabilityValue.Text("1.0.7"))
                {
                    Role = CapabilityRole.GenericReadOnly, ValueKind = CapabilityValueKind.Text
                }
            }.Select(CapabilityRowProjection.OnDevicePage)
        ], Recovery: DeviceHostRows.RecoveryView(DeviceCycleState.Faulted));

        static DeviceOverlayCapability OemButton(string id, string title, string assigned)
        {
            return new DeviceOverlayCapability(id, null, DescriptorStatus.Available, title,
                "What the button does", assigned == "overlay" ? "Overlay" : "Keyboard", true,
                CapabilityValue.Choice(assigned),
                CapabilityValue.Choice(assigned == "overlay" ? "keyboard" : "overlay"))
            {
                Role = CapabilityRole.OemControl, ValueKind = CapabilityValueKind.Choice, Writable = true,
                Choices =
                [
                    DeviceHostRows.HostChoice("overlay", "Overlay"), DeviceHostRows.HostChoice("keyboard", "Keyboard")
                ]
            };
        }
    }

    /// <summary>Which graphics package a Claw overlay runs beside.</summary>
    private enum ClawGpu
    {
        None,
        Published,
        Unpublished
    }

    /// <summary>
    ///     An overlay drawing the Claw publication on its Device destination, with the performance, power-preset
    ///     and Windows plan sources the Power page reads.
    /// </summary>
    private sealed class ClawOverlay : IAsyncDisposable
    {
        private readonly FixtureGraphicsSource? _gpu;
        private readonly FixtureGraphicsSource _graphics;
        private readonly SimulatedDeviceOverlaySource _hostControls;
        private readonly PerformanceService _performance;
        private readonly ChoiceRowSelection _schemes;
        private readonly DevicePowerPresetSelection _selection;
        private readonly UiFixture _fixture;

        private ClawOverlay(SimulatedDeviceOverlaySource hostControls, FixtureGraphicsSource graphics,
            FixtureGraphicsSource? gpu, FakeDevice device, DeviceCapabilityView[] views,
            PerformanceService performance, PerformanceOverlayBridge performanceBridge, UiFixture fixture,
            DevicePowerPresetSelection selection, ChoiceRowSelection schemes, OverlayWindow window)
        {
            _hostControls = hostControls;
            _graphics = graphics;
            _gpu = gpu;
            Device = device;
            Views = views;
            _performance = performance;
            PerformanceBridge = performanceBridge;
            _fixture = fixture;
            _selection = selection;
            _schemes = schemes;
            Window = window;
        }

        internal FakeDevice Device { get; }
        internal DeviceCapabilityView[] Views { get; }
        internal PerformanceOverlayBridge PerformanceBridge { get; }
        internal OverlayWindow Window { get; }

        public async ValueTask DisposeAsync()
        {
            _schemes.Dispose();
            _selection.Dispose();
            _fixture.Dispose();
            PerformanceBridge.Dispose();
            await _performance.DisposeAsync();
            Device.Dispose();
            _gpu?.Dispose();
            _graphics.Dispose();
            _hostControls.Dispose();
        }

        /// <summary>Opens the overlay on the Device destination's Overview.</summary>
        /// <param name="width">The window width.</param>
        /// <param name="height">The window height.</param>
        /// <param name="autoTdp">Whether AutoTDP is switched on, so Power carries the target slider.</param>
        /// <param name="gpu">The graphics package the GPU page draws.</param>
        internal static async Task<ClawOverlay> CreateAsync(int width, int height, bool autoTdp = false,
            ClawGpu gpu = ClawGpu.None)
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
            var sections = DeviceSectionLayout.IncludePredefined(publication.Descriptors.Sections);
            var ids = sections.Select(section => section.SectionId).ToHashSet();
            SimulatedDeviceOverlaySource hostControls = new();
            if (autoTdp)
            {
                // Switched on, so the Power page carries the target slider under the switch.
                await hostControls.SetHostSelectionAsync(DeviceHostRowIds.AutoTdp, "on");
            }

            // Variable refresh is the graphics package's now; Power and thermals shows it as the bridge does.
            var graphics = FixtureGraphicsSource.Load();
            var state = hostControls.Snapshot() with
            {
                Status = "MSI Claw 8 AI+",
                Detail = "Device integration active",
                Capabilities =
                [
                    .. views.Select(view => CapabilityRowProjection.ToDeviceRow(view, ids,
                        FanCurvePresetTable.HandheldCompanion)),
                    graphics.VariableRefreshRow(ids)
                ],
                PluginSections = CapabilityRowProjection.ProjectSections(sections),
                Recovery = null,
                // The bridge publishes the temperature the fan loop follows; here, the handheld's own sensor.
                FanTemperature = views.FirstOrDefault(view => view.Descriptor.CapabilityId == CapabilityIds.Temperature)
                    ?.Projection.State.ObservedValue?.IntegerValue
            };
            FakeDevice device = new() { SampleSource = hostControls, State = state };
            var profiles = ApplicationProfilesViewTests.Profiles();
            await profiles.SetAsync(values =>
            {
                values.FrameLimit = 60;
                values.OverlayLevel = 2;
            }, "fixture");
            var performance = ApplicationProfilesViewTests.Service(profiles);
            PerformanceOverlayBridge performanceBridge = new(performance, profiles);
            UiFixture fixture = new();
            var presets = new DevicePowerPresets(() => views,
                (_, _, _, _) => throw new InvalidOperationException("Unexpected hardware write"),
                new WindowsPowerModes(new PowerSchemes(new FakePower()), new ReadOnlyPowerModeApi()), () => true);
            ProfileConfig config = new()
            {
                Global = new ProfileValues
                {
                    AcPowerPreset = new DevicePowerPresetReference { PluginId = "claw", PresetId = "balanced" },
                    BatteryPowerPreset = new DevicePowerPresetReference
                        { PluginId = "claw", PresetId = "super-battery" }
                }
            };
            var assignments = new DevicePowerAssignments(presets,
                () => new DevicePowerAssignmentContext(new ProfileSnapshot(config, ActiveProfile.None, 1), "claw",
                    true, true),
                (_, _, _) => throw new InvalidOperationException("Unexpected assignment save"));
            DevicePowerPresetSelection selection = new(presets, false, assignments);
            await selection.RefreshAsync();
            var schemes = UiFixture.PowerProfiles(new FakePower());
            await schemes.RefreshAsync();
            var source = gpu switch
            {
                ClawGpu.Published => graphics,
                ClawGpu.Unpublished => FixtureGraphicsSource.Unpublished(),
                _ => null
            };
            var window = fixture.Overlay(width, height);
            window.AttachDeviceBridge(device);
            window.AttachPerformanceSource(performanceBridge);
            window.AttachPowerSchemes(schemes);
            window.AttachPowerPresets(selection);
            window.AttachGraphicsSource(source);
            await performance.RefreshAsync();
            UiFixture.Click(window, UiFixture.Tab(window, 2));
            return new ClawOverlay(hostControls, graphics, ReferenceEquals(source, graphics) ? null : source, device,
                views, performance, performanceBridge, fixture, selection, schemes, window);
        }
    }

    private sealed record Publication(CapabilityDescriptorSet Descriptors, CapabilityState[] States);
}
