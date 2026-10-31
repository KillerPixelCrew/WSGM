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
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class DevicePageCaptureTests
{
    [AvaloniaTheory]
    [InlineData("Device", 1280, 800)]
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
        using SimulatedDeviceOverlaySource hostControls = new();
        if (page == "AutoTDP")
        {
            // Switched on, so the Power page carries the target slider under the switch.
            await hostControls.ToggleAutoTdpAsync();
        }

        // Variable refresh is the graphics package's now; Power and thermals shows it as the bridge does.
        using var graphics = FixtureGraphicsSource.Load();
        var state = hostControls.Snapshot() with
        {
            Status = "MSI Claw 8 AI+",
            Detail = "Device integration active",
            Capabilities =
            [
                .. views.Select(view => CapabilityRowProjection.ToOverlayCapability(view, ids,
                    FanCurvePresetTable.HandheldCompanion)),
                graphics.VariableRefreshRow(ids)
            ],
            PluginSections = CapabilityRowProjection.ProjectSections(sections),
            Recovery = null
        };
        using FakeDevice device = new();
        device.SampleSource = hostControls;
        device.State = state;
        var profiles = ApplicationProfilesViewTests.Profiles();
        await profiles.SetAsync(values =>
        {
            values.FrameLimit = 60;
            values.OverlayLevel = 2;
        }, "fixture");
        await using var performance = ApplicationProfilesViewTests.Service(profiles);
        using PerformanceOverlayBridge performanceBridge = new(performance, profiles);
        using UiFixture fixture = new();
        var presets = new DevicePowerPresets(() => views,
            (_, _, _, _) => throw new InvalidOperationException("Unexpected hardware write"),
            new WindowsPowerModes(new PowerSchemes(new FakePower()), new ReadOnlyPowerModeApi()), () => true);
        ProfileConfig config = new()
        {
            Global = new ProfileValues
            {
                AcPowerPreset = new DevicePowerPresetReference { PluginId = "claw", PresetId = "balanced" },
                BatteryPowerPreset = new DevicePowerPresetReference { PluginId = "claw", PresetId = "super-battery" }
            }
        };
        var assignments = new DevicePowerAssignments(presets,
            () => new DevicePowerAssignmentContext(new ProfileSnapshot(config, ActiveProfile.None, 1), "claw",
                true, true),
            (_, _, _) => throw new InvalidOperationException("Unexpected assignment save"));
        using DevicePowerPresetSelection selection = new(presets, false, assignments);
        await selection.RefreshAsync();
        using PowerSchemeSelection schemes = new(new NativeQamPowerProfileService(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Unexpected power plan write")));
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
            UiFixture.Click(window, UiFixture.Rail(window, "device.section.plugin.power"));
            UiFixture.OpenSections(window);
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
        }
        else if (page != "Device")
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
            Assert.True(UiFixture.Named<StackPanel>(window, "DevicePowerPresetContainer").IsEffectivelyVisible);
            Assert.Contains(power.GetVisualDescendants().OfType<Control>(),
                control => Equals(control.Tag, "power.primary-limit"));
            var toggle = UiFixture.Named<StackPanel>(window, "DeviceApplicationProfileHost");
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
            Assert.True(UiFixture.Named<StackPanel>(window, "DeviceWindowsPower").IsEffectivelyVisible);
            var cards = window.GetVisualDescendants().OfType<CollapsibleSection>()
                .Where(section => section.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(cards);
            var detail = UiFixture.Named<Control>(window, "DevicePowerOverview");
            Assert.All(cards, card => Assert.InRange(card.Bounds.Width, 240, detail.Bounds.Width));
            var power = Assert.Single(cards, card => Equals(card.Heading.Tag, "section.device.unified-power.expand"));
            Assert.DoesNotContain(cards, card => Equals(card.Heading.Tag, "section.system.power-profile.expand"));
            // The Claw's fan group is its own fold after Power, not one of Power's open groups.
            var fans = Assert.Single(cards, card => Equals(card.Heading.Tag,
                "section.device.plugin.power.category.control.expand"));
            Assert.DoesNotContain(fans, power.GetVisualDescendants());
            Assert.Contains(fans, UiFixture.Named<Panel>(window, "DeviceFansHost").GetVisualDescendants());
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

            var planGroup = UiFixture.Named<StackPanel>(window, "DeviceWindowsPower").GetVisualAncestors()
                .OfType<Border>().First(border => border.Classes.Contains("device-group"));
            var assignmentsGroup = UiFixture.Named<StackPanel>(window, "DevicePowerPresetContainer")
                .GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("device-group"));
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
            var detail = UiFixture.Named<Control>(window, "DevicePowerOverview");
            Assert.True(slider.TranslatePoint(new Point(slider.Bounds.Width, 0), detail)!.Value.X <= detail.Bounds.Width,
                "The target slider fits the controls pane.");
            slider.BringIntoView();
            Dispatcher.UIThread.RunJobs();
        }

        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", "claw-" +
            page.ToLowerInvariant().Replace(' ', '-')
            + $"-{width}x{height}");
        Directory.CreateDirectory(directory);
        Capture(window, Path.Combine(directory, "viewport.png"));
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
                await performanceBridge.SetValueAsync("frame-limit", 61 + update);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(before, scroll.Offset.Y, 1);
            }

            scroll.Offset = default;
        }

        window.Height += Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        Dispatcher.UIThread.RunJobs();
        Capture(window, Path.Combine(directory, "full.png"));
        Assert.True(views.Length >= 16);
        window.Close();
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

    private sealed record Publication(CapabilityDescriptorSet Descriptors, CapabilityState[] States);
}
