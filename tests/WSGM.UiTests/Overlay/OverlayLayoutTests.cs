using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowsDeviceControl;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class OverlayLayoutTests
{
    [AvaloniaFact]
    public void PinnedActionKeepsFocusWhenDevicePublishesReadback()
    {
        using FakeDevice device = new();
        device.State = device.State with { Capabilities = [device.State.Capabilities[0] with { CanInvoke = true }] };
        device.Invoke = (_, _) =>
        {
            device.Notify();
            return Task.CompletedTask;
        };
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.SetPins(["section.device.overview"]);
        var panel = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        Dispatcher.UIThread.RunJobs();
        Action().Focus();
        UiFixture.Click(window, Action());
        Dispatcher.UIThread.RunJobs();
        Assert.True(Action().IsFocused);

        return;

        CardButton Action()
        {
            return panel.GetVisualDescendants().OfType<CardButton>().Single();
        }
    }

    [AvaloniaFact]
    public void ReadOnlySectionPinsThroughItsHeadingWithoutADeviceCommand()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card is { IsEffectivelyVisible: true, Title: "Overview" }));
        var reading = window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card is { IsEffectivelyVisible: true, Title: "Processor temperature" });
        List<string> pins = [];
        window.PinToggleRequested += pins.Add;
        Assert.False(reading.IsEffectivelyEnabled);
        Assert.True(Header().Focus());
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Header().IsFocused);
        UiFixture.Click(window, Header());
        Assert.Equal(["section.device.overview"], pins);

        return;

        Button Header()
        {
            return window.GetVisualDescendants().OfType<SectionPinHeader>()
                .Single(header => header.IsEffectivelyVisible).GetVisualDescendants().OfType<Button>().Single();
        }
    }

    [AvaloniaFact]
    public async Task WindowsPowerPickerPinsWithoutDeviceIntegrationAndDetachesOnRemoval()
    {
        using PowerSchemeSelection schemes = new(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Pinning must not apply a power plan"));
        await schemes.RefreshAsync();
        using FakeDevice device = new();
        device.State = device.State with { Visible = false };
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.AttachPowerSchemes(schemes);
        window.SetPins(["section.system.power-profile"]);
        Dispatcher.UIThread.RunJobs();
        var panel = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        Assert.Single(panel.GetVisualDescendants().OfType<PowerSchemeView>());
        var choice = panel.GetVisualDescendants().OfType<ComboBox>().Single();
        UiFixture.Tab(window, 0).Focus();
        device.State = device.State with { Visible = true };
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(choice, panel.GetVisualDescendants().OfType<ComboBox>().Single());
        List<string> pins = [];
        window.PinToggleRequested += pins.Add;
        choice.Focus();
        window.RequestSecondaryAction(choice);
        Assert.Equal(["section.system.power-profile"], pins);
        window.SetPins([]);
        Assert.Empty(panel.Children);
        await schemes.RefreshAsync();
        Assert.Empty(panel.Children);
    }

    [AvaloniaTheory]
    [InlineData(CapabilityValueKind.Integer)]
    [InlineData(CapabilityValueKind.Boolean)]
    [InlineData(CapabilityValueKind.Choice)]
    [InlineData(CapabilityValueKind.Curve)]
    public void SectionPinIncludesAllItsControlsAndPreservesTheirEditors(CapabilityValueKind kind)
    {
        using FakeDevice device = new();
        List<DeviceOverlayCapability> writes = [];
        device.Invoke = (capability, _) =>
        {
            writes.Add(capability);
            return Task.CompletedTask;
        };
        using UiFixture fixture = new();
        device.State = device.State with
        {
            Capabilities =
            [
                device.State.Capabilities[0],
                new DeviceOverlayCapability("fixture.other", null, DeviceOverlaySection.Oem, DescriptorStatus.Available,
                    "Other section", "", "", false),
                new DeviceOverlayCapability("fixture.value", null, DeviceOverlaySection.Overview,
                    DescriptorStatus.Available, "Fixture value", "", "", true,
                    new CapabilityValue
                    {
                        Kind = kind, IntegerValue = 50, BooleanValue = false, ChoiceValue = "a",
                        CurveValue = [new CurvePoint(30, 20), new CurvePoint(60, 50), new CurvePoint(90, 100)]
                    })
                {
                    ValueKind = kind, Writable = true, Minimum = 0, Maximum = 100, Step = 1,
                    Choices =
                    [
                        new CapabilityChoice("a",
                            new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "First" }),
                        new CapabilityChoice("b",
                            new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Second" })
                    ]
                }
            ]
        };
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card is { IsEffectivelyVisible: true, Title: "Overview" }));
        var type = kind switch
        {
            CapabilityValueKind.Integer => typeof(Slider),
            CapabilityValueKind.Boolean => typeof(ToggleSwitch),
            CapabilityValueKind.Curve => typeof(CurveEditor),
            _ => typeof(ComboBox)
        };
        var editor = window.GetVisualDescendants().OfType<Control>()
            .Single(control => control.GetType() == type && control.IsEffectivelyVisible);
        List<string> requests = [];
        window.PinToggleRequested += requests.Add;
        editor.Focus();
        window.RequestSecondaryAction(editor);
        Assert.Equal(["section.device.overview"], requests);
        UiFixture.Click(window, editor, MouseButton.Right);
        Assert.Equal(2, requests.Count);
        switch (editor)
        {
            case Slider slider:
                Assert.Equal(50, slider.Value);
                break;
            case ComboBox choice:
                Assert.False(choice.IsDropDownOpen);
                break;
        }

        var header = window.GetVisualDescendants().OfType<SectionPinHeader>()
            .Single(header => header.IsEffectivelyVisible);
        UiFixture.Click(window, header.GetVisualDescendants().OfType<Button>().Single());
        Assert.Equal(3, requests.Count);
        Assert.All(requests, id => Assert.Equal("section.device.overview", id));
        window.SetPins(["section.device.overview"]);
        UiFixture.Click(window, UiFixture.Tab(window, 0));
        var pinned = UiFixture.Named<Panel>(window, "PinnedSectionsGrid").GetVisualDescendants().OfType<Control>()
            .Single(control => control.GetType() == type);
        Assert.Equal("pin:fixture.value", pinned.Tag);
        var pinnedSection = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        Assert.Contains(pinnedSection.GetVisualDescendants().OfType<CardButton>(),
            row => row.Title == "Processor temperature");
        Assert.DoesNotContain(pinnedSection.GetVisualDescendants().OfType<CardButton>(),
            row => row.Title == "Other section");
        pinned.Focus();
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(pinnedSection.GetVisualDescendants(), control => ReferenceEquals(control, pinned));
        Assert.Empty(writes);
        if (pinned is ToggleSwitch toggle)
        {
            UiFixture.Click(window, toggle);
            Assert.True(Assert.Single(writes).NextValue?.BooleanValue);
        }

        window.RequestSecondaryAction(pinned);
        Assert.Equal("section.device.overview", requests[^1]);
        device.State = device.State with { Visible = false };
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(pinnedSection.GetVisualDescendants(), control => control.GetType() == type);
        var unavailableHeader = pinnedSection.GetVisualDescendants().OfType<SectionPinHeader>().Single();
        UiFixture.Click(window, unavailableHeader.GetVisualDescendants().OfType<Button>().Single());
        Assert.Equal("section.device.overview", requests[^1]);
        window.SetPins([]);
        Assert.DoesNotContain(UiFixture.Named<Panel>(window, "PinnedSectionsGrid").GetVisualDescendants(),
            control => control.GetType() == type);
    }

    [AvaloniaFact]
    public async Task DisplayCategoryHasReadableWidthAndLabeledSelectors()
    {
        using UiFixture fixture = new();
        using NativeQamBrightnessService brightness = new(() => true, () => { }, () => 60,
            _ => throw new InvalidOperationException("Unexpected write"), Timeout.InfiniteTimeSpan);
        await brightness.ReadAsync();
        var window = fixture.Overlay();
        var host = UiFixture.Named<StackPanel>(window, "DisplayBrightnessHost");
        DisplayTargetIdentity target = new("fixture", null, null, "Internal display", 1, 0, 1);
        DisplayModeSnapshot modes = new(new ActiveDisplayPath(target, "fixture", 0, 120, 1),
            new DisplayMode(1920, 1200, 120),
            [new DisplayMode(1920, 1200, 60), new DisplayMode(1920, 1200, 120), new DisplayMode(1280, 800, 60)]);
        window.AttachBrightness(brightness, () => Task.FromResult<DisplayModeSnapshot?>(modes));
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card is { IsEffectivelyVisible: true, Title: "Display" }));
        Assert.Equal(720, host.Bounds.Width);
        VisualBaseline.Verify(window, "overlay-display-1280");
        window.SetPins(["section.display"]);
        UiFixture.Click(window, UiFixture.Tab(window, 0));
        var section = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        Assert.Single(section.GetVisualDescendants().OfType<Slider>());
        Assert.Equal(2, section.GetVisualDescendants().OfType<ComboBox>().Count());
    }
}
