using Avalonia;
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
using WSGM.Settings;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class OverlayLayoutTests
{
    [AvaloniaFact]
    public void PinnedActionKeepsFocusWhenDevicePublishesReadback()
    {
        using FakeDevice device = new();
        device.State = device.State with { Capabilities = [device.State.Capabilities[0] with { CanInvoke = true }] };
        device.Invoke = (_, _) => { device.Notify(); return Task.CompletedTask; };
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        window.SetPins(["section.device.overview"]);
        var panel = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        Dispatcher.UIThread.RunJobs();
        CardButton Action() => panel.GetVisualDescendants().OfType<CardButton>().Single();
        Action().Focus();
        UiFixture.Click(window, Action());
        Dispatcher.UIThread.RunJobs();
        Assert.True(Action().IsFocused);
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
            .Single(card => card.IsEffectivelyVisible && card.Title == "Overview"));
        var reading = window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Processor temperature");
        List<string> pins = [];
        window.PinToggleRequested += pins.Add;
        Assert.False(reading.IsEffectivelyEnabled);
        Button Header() => window.GetVisualDescendants().OfType<SectionPinHeader>()
            .Single(header => header.IsEffectivelyVisible).GetVisualDescendants().OfType<Button>().Single();
        Assert.True(Header().Focus());
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.True(Header().IsFocused);
        UiFixture.Click(window, Header());
        Assert.Equal(["section.device.overview"], pins);
    }

    [AvaloniaFact]
    public async Task WindowsPowerPickerPinsWithoutDeviceIntegrationAndDetachesOnRemoval()
    {
        using PowerSchemeSelection schemes = new(new PowerSchemes(new OverlayInteractionTests.FakePower()),
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
        device.Invoke = (capability, _) => { writes.Add(capability); return Task.CompletedTask; };
        using UiFixture fixture = new();
        device.State = device.State with
        {
            Capabilities = [device.State.Capabilities[0],
                new("fixture.other", null, DeviceOverlaySection.Oem, DescriptorStatus.Available, "Other section", "", "", false),
                new("fixture.value", null, DeviceOverlaySection.Overview,
                DescriptorStatus.Available, "Fixture value", "", "", true,
                new() { Kind = kind, IntegerValue = 50, BooleanValue = false, ChoiceValue = "a", CurveValue = [new(30, 20), new(60, 50), new(90, 100)] })
            {
                ValueKind = kind, Writable = true, Minimum = 0, Maximum = 100, Step = 1,
                Choices = [new("a", new() { Key = DisplayKey.Custom, CustomLabel = "First" }),
                    new("b", new() { Key = DisplayKey.Custom, CustomLabel = "Second" })],
            }],
        };
        OverlayWindow window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Overview"));
        Type type = kind switch
        {
            CapabilityValueKind.Integer => typeof(Slider),
            CapabilityValueKind.Boolean => typeof(ToggleSwitch),
            CapabilityValueKind.Curve => typeof(CurveEditor),
            _ => typeof(ComboBox),
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
        if (editor is Slider slider) { Assert.Equal(50, slider.Value); }
        if (editor is ComboBox choice) { Assert.False(choice.IsDropDownOpen); }
        var header = window.GetVisualDescendants().OfType<SectionPinHeader>().Single(header => header.IsEffectivelyVisible);
        UiFixture.Click(window, header.GetVisualDescendants().OfType<Button>().Single());
        Assert.Equal(3, requests.Count);
        Assert.All(requests, id => Assert.Equal("section.device.overview", id));
        window.SetPins(["section.device.overview"]);
        UiFixture.Click(window, UiFixture.Tab(window, 0));
        var pinned = UiFixture.Named<Panel>(window, "PinnedSectionsGrid").GetVisualDescendants().OfType<Control>()
            .Single(control => control.GetType() == type);
        Assert.Equal("pin:fixture.value", pinned.Tag);
        var pinnedSection = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        Assert.Contains(pinnedSection.GetVisualDescendants().OfType<CardButton>(), row => row.Title == "Processor temperature");
        Assert.DoesNotContain(pinnedSection.GetVisualDescendants().OfType<CardButton>(), row => row.Title == "Other section");
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
        Assert.DoesNotContain(UiFixture.Named<Panel>(window, "PinnedSectionsGrid").GetVisualDescendants(), control => control.GetType() == type);
    }

    [AvaloniaTheory]
    [InlineData(1024)]
    [InlineData(1280)]
    public void DisplaySettingsShowsReadableModesAndLabeledScaling(int width)
    {
        DisplayTargetIdentity target = new("fixture", null, null, "Internal display", 0, 0, 1);
        using UiFixture fixture = new()
        {
            Displays = new([new(target, true, true,
                new(target, 0, 0, 1920, 1200, DisplayRefresh.FromHertz(120), DpiPercent: 150, Hdr: true))],
                "fixture", DateTimeOffset.UnixEpoch),
        };
        fixture.DisplayFacts[target.DevicePath] = new([new(1920, 1200, 120), new(1280, 800, 60)], true, 225);
        var window = fixture.Settings(width, 800);
        UiFixture.Click(window, UiFixture.Tab(window, 6));
        var model = Assert.IsType<SettingsViewModel>(window.DataContext);
        model.GameModeLaunchKindIndex = (int)GameModeLaunchKind.Custom;
        model.SnapshotGameLayoutCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var text = window.GetVisualDescendants().OfType<TextBlock>().Where(control => control.IsEffectivelyVisible).ToArray();
        Assert.DoesNotContain(text, control => control.Text?.Contains("DisplayMode {") == true);
        Assert.Contains(text, control => control.Text == "Scale (%)");
        VisualBaseline.Verify(window, "settings-display-custom-" + width);
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
        DisplayModeSnapshot modes = new(new(target, "fixture", 0, 120, 1), new(1920, 1200, 120),
            [new(1920, 1200, 60), new(1920, 1200, 120), new(1280, 800, 60)]);
        window.AttachBrightness(brightness, () => Task.FromResult<DisplayModeSnapshot?>(modes));
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        UiFixture.Click(window, window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Display"));
        Assert.Equal(720, host.Bounds.Width);
        VisualBaseline.Verify(window, "overlay-display-1280");
        window.SetPins(["section.display"]);
        UiFixture.Click(window, UiFixture.Tab(window, 0));
        var section = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        Assert.Single(section.GetVisualDescendants().OfType<Slider>());
        Assert.Equal(2, section.GetVisualDescendants().OfType<ComboBox>().Count());
    }

    [AvaloniaFact]
    public void SettingsTabsScrollToTheirFullLabels()
    {
        using UiFixture fixture = new();
        Window window = fixture.Settings(1024, 700);
        var tabs = UiFixture.Named<TabStrip>(window, "Tabs");
        for (int i = 0; i < tabs.Tabs!.Count; i++)
        {
            UiFixture.Click(window, UiFixture.Tab(window, i));
            Dispatcher.UIThread.RunJobs();
            Button button = UiFixture.Tab(window, i);
            var label = button.GetVisualDescendants().OfType<TextBlock>().Single();
            Assert.Equal(tabs.Tabs[i].Label, label.Text);
            Assert.True(button.Bounds.Width >= label.Bounds.Width + 12);
            var origin = button.TranslatePoint(default, tabs)!.Value;
            Assert.InRange(origin.X, 0, tabs.Bounds.Width - button.Bounds.Width);
        }
    }
}
