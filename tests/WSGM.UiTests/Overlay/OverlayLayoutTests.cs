using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
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
using Path = Avalonia.Controls.Shapes.Path;

namespace WSGM.UiTests.Overlay;

public sealed class OverlayLayoutTests
{
    [AvaloniaFact]
    public void BatteryGlyphAndPercentageShareTheSameVerticalCentre()
    {
        using var fixture = new UiFixture();
        var window = fixture.Overlay(1280, 720);
        var status = UiFixture.Named<Border>(window, "BatteryStatus");
        status.IsVisible = true;
        var label = UiFixture.Named<TextBlock>(window, "BatteryLabel");
        label.Text = "67%";
        Dispatcher.UIThread.RunJobs();
        var glyph = status.GetVisualDescendants().OfType<Path>().Single();
        Assert.InRange(Math.Abs(glyph.Bounds.Center.Y - label.Bounds.Center.Y), 0, 1);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1280, 720, 1.25)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(1280, 720, 2.0)]
    [InlineData(3840, 2160, 2.0)]
    [InlineData(3840, 2160, 3.0)]
    public void NativeDesktopDpiRetainsTheWorkspaceFloorAndPhysicalKeyboardTargets(int width, int height,
        double nativeScale)
    {
        using UiFixture fixture = new();
        // Model the DIP viewport Windows provides at this physical resolution and native DPI.
        var window = fixture.Overlay(width, height, 1.0, nativeScale);
        var workspace = UiFixture.Named<Grid>(window, "SurfaceRoot");
        Assert.True(workspace.Bounds.Width >= 979.5);
        Assert.True(workspace.Bounds.Height >= 639.5);
        var keyboard = new KeyboardPanel("Text entry", "", 100);
        window.ShowKeyboardSurface(keyboard);
        Dispatcher.UIThread.RunJobs();
        foreach (var button in keyboard.GetVisualDescendants().OfType<Button>()
                     .Where(button => button.IsEffectivelyVisible))
        {
            var origin = button.TranslatePoint(default, window)!.Value;
            var far = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
            Assert.InRange(origin.X, 0, window.Bounds.Width);
            Assert.InRange(origin.Y, 0, window.Bounds.Height);
            Assert.InRange(far.X, 0, window.Bounds.Width + 0.1);
            Assert.InRange(far.Y, 0, window.Bounds.Height + 0.1);
            Assert.True((far.X - origin.X) * nativeScale >= 43.5);
            Assert.True((far.Y - origin.Y) * nativeScale >= 43.5);
        }
    }

    [AvaloniaTheory]
    [InlineData(1280, 720, 1.5)]
    [InlineData(1920, 1080, 1.5)]
    [InlineData(3840, 2160, 2.0)]
    [InlineData(3840, 2160, 3.0)]
    public void SavedGameModeScaleMatchesNativeDesktopPhysicalSizing(int width, int height, double desiredScale)
    {
        var gameMode = OverlayWindow.ComputeContentScale(desiredScale, 1, width, height);
        var desktop = OverlayWindow.ComputeContentScale(1, desiredScale, width, height) * desiredScale;
        Assert.Equal(gameMode, desktop, 5);
    }

    [AvaloniaTheory]
    [InlineData(980, 640, 1.0)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(3840, 2160, 2.0)]
    public void PopulatedStatusHeaderKeepsUtilitiesAndCloseSeparateFromTheTitle(int width, int height, double scale)
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay(width, height, scale);
        UiFixture.Named<TextBlock>(window, "WifiLabel").Text = "A very long wireless network name";
        UiFixture.Named<TextBlock>(window, "WifiLabel").IsVisible = true;
        UiFixture.Named<TextBlock>(window, "VolumeLabel").Text = "100%";
        UiFixture.Named<TextBlock>(window, "BatteryLabel").Text = "100%";
        UiFixture.Named<TextBlock>(window, "ClockLabel").Text = "23:59";
        UiFixture.Named<Border>(window, "BatteryStatus").IsVisible = true;
        UiFixture.Named<Button>(window, "EjectButton").IsVisible = true;
        UiFixture.Named<Button>(window, "BackButton").IsVisible = true;
        UiFixture.Named<TextBlock>(window, "ProfileContext").Text = "Profile: a-very-long-application.exe";
        Dispatcher.UIThread.RunJobs();
        var title = UiFixture.Named<ComboBox>(window, "HeaderProfile");
        var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), window)!.Value.X;
        var buttons = new[]
        {
            "ManageProfiles", "WifiButton", "BluetoothButton", "AudioButton", "BrightnessButton",
            "EjectButton", "KeyboardButton", "CloseButton"
        }.Select(name => UiFixture.Named<Button>(window, name)).ToArray();
        var previousRight = titleRight;
        foreach (var button in buttons)
        {
            var left = button.TranslatePoint(default, window)!.Value.X;
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
            Assert.True(left >= previousRight, button.Name);
            Assert.InRange(right, 0, width);
            Assert.True(button.Bounds.Width >= 44 && button.Bounds.Height >= 44, button.Name);
            previousRight = right;
        }
    }

    [AvaloniaTheory]
    [InlineData(980, 640, 1.0)]
    [InlineData(1280, 720, 1.0)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(1920, 1080, 1.5)]
    [InlineData(3840, 2160, 1.0)]
    [InlineData(3840, 2160, 2.0)]
    public void FullscreenWorkspaceKeepsHeaderRailControlsAndFooterInsideTheViewport(
        int width, int height, double uiScale)
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay(width, height, uiScale);
        UiFixture.Click(window, UiFixture.Tab(window, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(width, window.ClientSize.Width);
        Assert.Equal(height, window.ClientSize.Height);
        foreach (var name in new[] { "Header", "CloseButton", "SectionRail", "ContentScroller", "AppsStrip" })
        {
            var control = UiFixture.Named<Control>(window, name);
            var topLeft = control.TranslatePoint(default, window)!.Value;
            var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window)!
                .Value;
            Assert.InRange(topLeft.X, -1, width);
            Assert.InRange(topLeft.Y, -1, height);
            Assert.InRange(bottomRight.X, 0, width + 1);
            Assert.InRange(bottomRight.Y, 0, height + 1);
            Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, name);
        }

        var rail = UiFixture.Named<StackPanel>(window, "SectionRail");
        var rows = rail.Children.OfType<Button>().ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(48, row.Bounds.Height));
        Assert.All(rows, row => Assert.Equal(rows[0].Bounds.Width, row.Bounds.Width));
        Assert.Equal(4, rail.Spacing);
        var content = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
        var railRight = rail.TranslatePoint(new Point(rail.Bounds.Width, 0), window)!.Value.X;
        var contentLeft = content.TranslatePoint(default, window)!.Value.X;
        Assert.True(contentLeft > railRight);
        Assert.True(content.Bounds.Width > rail.Bounds.Width);
    }

    [AvaloniaFact]
    public void UnavailableNativeBackdropUsesAnOpaqueCanvasAndDistinctControlPlane()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        Assert.Equal(WindowTransparencyLevel.Transparent, window.ActualTransparencyLevel);
        var canvas =
            Assert.IsAssignableFrom<ISolidColorBrush>(UiFixture.Named<Border>(window, "GlassCanvas").Background);
        Assert.Equal(byte.MaxValue, canvas.Color.A);
        var controls = Assert.IsType<Border>(UiFixture.Named<ScrollViewer>(window, "ContentScroller").Parent);
        var plane = Assert.IsAssignableFrom<ISolidColorBrush>(controls.Background);
        Assert.NotEqual(canvas.Color, plane.Color);
        Assert.Equal(new[] { WindowTransparencyLevel.Transparent },
            window.TransparencyLevelHint);
    }

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
        var original = Action();
        Assert.True(original.Focus(), "Initial focus refused");
        UiFixture.Click(window, original);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(original, Action());
        Assert.True(Action().IsFocused);

        return;

        Button Action()
        {
            return Assert.IsType<Button>(panel.GetVisualDescendants().OfType<DeviceSettingRow>().Single().Editor);
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
        UiFixture.Click(window, UiFixture.Rail(window, "device.section.overview"));
        var reading = window.GetVisualDescendants().OfType<DeviceStatisticRow>()
            .Single(row => row.IsEffectivelyVisible);
        List<string> pins = [];
        window.PinToggleRequested += pins.Add;
        Assert.False(reading.Focusable);
        Assert.DoesNotContain(reading.GetVisualDescendants().OfType<Button>(),
            button => button.IsEffectivelyVisible && button.Focusable);
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
        UiFixture.Click(window, UiFixture.Rail(window, "device.section.overview"));
        var type = kind switch
        {
            CapabilityValueKind.Integer => typeof(Slider),
            CapabilityValueKind.Boolean => typeof(ToggleSwitch),
            CapabilityValueKind.Curve => typeof(CurveEditor),
            _ => typeof(ComboBox)
        };
        var editor = UiFixture.Named<ScrollViewer>(window, "ContentScroller").GetVisualDescendants().OfType<Control>()
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
        Assert.Contains(pinnedSection.GetVisualDescendants().OfType<TextBlock>(),
            row => row.Text == "Processor temperature");
        Assert.DoesNotContain(pinnedSection.GetVisualDescendants().OfType<TextBlock>(),
            row => row.Text == "Other section");
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
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemDisplay));
        var content = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
        Assert.InRange(host.Bounds.Width, 400, content.Bounds.Width);
        VisualBaseline.Verify(window, "overlay-display-1280");
        window.SetPins(["section.display"]);
        UiFixture.Click(window, UiFixture.Tab(window, 0));
        var section = UiFixture.Named<Panel>(window, "PinnedSectionsGrid");
        Assert.Single(section.GetVisualDescendants().OfType<Slider>());
        Assert.Equal(2, section.GetVisualDescendants().OfType<ComboBox>().Count());
    }
}
