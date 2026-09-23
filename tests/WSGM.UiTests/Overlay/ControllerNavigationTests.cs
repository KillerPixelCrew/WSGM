using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Input;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Tests.Fakes;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class ControllerNavigationTests
{
    [AvaloniaFact]
    public void RetractingTheOpenDeviceSectionSelectsAndFocusesTheSurvivingOverview()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        var sensor = device.State.Capabilities[0];
        var lighting = new DeviceOverlayCapability("fixture.lighting", null,
            DeviceOverlaySection.LightingAndFeatures, DescriptorStatus.Available,
            "Lighting", "", "Off", false) { PluginSectionId = DeviceSections.RgbId };
        device.State = device.State with { Capabilities = [sensor, lighting] };
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        var removed = UiFixture.Rail(window, "device.section.plugin." + DeviceSections.RgbId);
        UiFixture.Click(window, removed);
        Assert.True(removed.IsFocused);
        Assert.Contains("selected", removed.Classes);

        device.State = device.State with { Capabilities = [sensor] };
        device.Notify();
        Dispatcher.UIThread.RunJobs();

        var overview = UiFixture.Rail(window, "device.overview");
        Assert.Contains("selected", overview.Classes);
        Assert.True(overview.IsFocused);
        Assert.DoesNotContain(UiFixture.Named<StackPanel>(window, "SectionRail").Children,
            control => Equals(control.Tag, "rail.device.section.plugin." + DeviceSections.RgbId));
        Assert.NotNull(UiFixture.Rail(window, "device.section.overview"));
        Assert.True(UiFixture.Named<Control>(window, "PanelDevice").IsVisible);
    }

    [AvaloniaFact]
    public void KeyboardSelectorCommitsHighlightedChoiceWithNavigationAttachedAndSuppressesMirroredPad()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        FakeButtonSource buttons = new();
        using GamepadNavigation navigation = new(buttons, window, () => window.TryCancelSubView(),
            preferredFocus: () => window.DefaultFocusTarget);
        var vm = Assert.IsType<OverlayViewModel>(window.DataContext);
        vm.PowerTimeoutValues = new Dictionary<PowerTimeoutKind, int?> { [PowerTimeoutKind.DisplayDc] = 60 };
        List<(PowerTimeoutKind Kind, int Seconds)> requested = [];
        window.PowerTimeoutSelected += (kind, seconds) => requested.Add((kind, seconds));
        UiFixture.Click(window, UiFixture.Tab(window, 3));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.PowerTimeouts));
        var editor = UiFixture.Named<StackPanel>(window, "PowerTimeoutEditors")
            .GetVisualDescendants().OfType<ComboBox>().Single(choice => choice.IsEnabled);
        Assert.True(editor.Focus());
        UiFixture.Key(window, Key.Enter);
        Assert.True(editor.IsDropDownOpen);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Down);
        Assert.Empty(requested);
        UiFixture.Key(window, Key.Enter);
        Assert.False(editor.IsDropDownOpen);
        Assert.Equal((PowerTimeoutKind.DisplayDc, 300), Assert.Single(requested));
        buttons.Press(GamepadButtons.A);
        Assert.False(editor.IsDropDownOpen);
        Assert.Single(requested);
    }

    [AvaloniaFact]
    public async Task ControllerRailOpensGlyphsAndExplainsUnavailableOutputWithoutIntegration()
    {
        using SimulatedDeviceOverlaySource source = new();
        var state = source.Snapshot() with
        {
            Visible = false,
            Capabilities = [],
            Controller = null,
            PluginSections = DeviceOverlayBridge.ProjectSections(DeviceSections.IncludePredefined([]))
        };
        using FakeDevice device = new();
        device.SampleSource = source;
        device.State = state;
        using UiFixture fixture = new();
        using PowerSchemeSelection schemes = new(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Unexpected power write"));
        await schemes.RefreshAsync();
        var window = fixture.Overlay();
        DeviceGlyphSelection? requested = null;
        device.SelectGlyphs = selection =>
        {
            requested = selection;
            return Task.CompletedTask;
        };
        window.AttachDeviceBridge(device);
        window.AttachPowerSchemes(schemes);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        var controller = UiFixture.Rail(window, "device.section.plugin." + DeviceSections.ControllerId);
        UiFixture.Click(window, controller);
        Dispatcher.UIThread.RunJobs();
        Assert.True(controller.IsEffectivelyVisible);
        Assert.Contains("selected", controller.Classes);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text?.Contains("Device integration is off.") is true);
        Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(),
            choice => choice is { IsEffectivelyVisible: true, Tag: "device.glyph-selection" });
        Assert.Null(requested);
        var glyphs = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(choice => choice is { IsEffectivelyVisible: true, Tag: "device.glyph-selection" });
        UiFixture.Click(window, glyphs);
        UiFixture.Key(window, Key.Down);
        UiFixture.Key(window, Key.Enter);
        Assert.Equal(DeviceGlyphSelection.NativeSteam, requested);
        UiFixture.Key(window, Key.Escape);
        Assert.True(controller.IsFocused);
        Assert.Contains("selected", controller.Classes);
    }

    [AvaloniaTheory]
    [InlineData(GamepadButtons.LeftShoulder, GamepadButtons.RightShoulder)]
    [InlineData(GamepadButtons.LeftTrigger, GamepadButtons.RightTrigger)]
    public void ControllerSwitchesDestinationsFromAnOpenPrimarySection(
        GamepadButtons previous, GamepadButtons next)
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        FakeButtonSource buttons = new();
        using GamepadNavigation navigation = new(buttons, window, () => window.TryCancelSubView(),
            preferredFocus: () => window.DefaultFocusTarget,
            tabPrevious: window.SelectPreviousTab, tabNext: window.SelectNextTab,
            navigate: window.NavigateWorkspace, triggerTabs: true);
        UiFixture.Click(window, UiFixture.Tab(window, 1));
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SteamLaunchFixes));
        buttons.Press(next);
        Assert.True(UiFixture.Named<Control>(window, "PanelSystemTools").IsVisible);
        buttons.Press(previous);
        Assert.True(UiFixture.Named<Control>(window, "PanelSteamLaunch").IsVisible);
        Assert.Contains("selected", UiFixture.Rail(window, OverlayPage.SteamLaunchFixes).Classes);
    }

    [AvaloniaFact]
    public void ControllerEntersControlsAndReturnsToTheSelectedRailBeforeHome()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        FakeButtonSource buttons = new();
        using GamepadNavigation navigation = new(buttons, window, () => window.TryCancelSubView(),
            preferredFocus: () => window.DefaultFocusTarget,
            tabPrevious: window.SelectPreviousTab, tabNext: window.SelectNextTab,
            navigate: window.NavigateWorkspace, triggerTabs: true);
        UiFixture.Click(window, UiFixture.Tab(window, 1));
        var rail = UiFixture.Rail(window, OverlayPage.SteamLibrary);
        Assert.True(rail.Focus());
        buttons.Press(GamepadButtons.DPadRight);
        var focused = Assert.IsAssignableFrom<Control>(window.FocusManager.GetFocusedElement());
        Assert.Contains(UiFixture.Named<Control>(window, "PanelSteamLibrary"), focused.GetVisualAncestors());
        buttons.Press(GamepadButtons.B);
        Assert.Same(rail, window.FocusManager.GetFocusedElement());
        Assert.True(UiFixture.Named<Control>(window, "PanelSteamLibrary").IsVisible);
        buttons.Press(GamepadButtons.B);
        Assert.True(UiFixture.Named<Control>(window, "PanelQuickAccess").IsVisible);
    }

    [AvaloniaFact]
    public void ControllerDirectionsStayInAnOpenUtilitySurface()
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        FakeButtonSource buttons = new();
        using GamepadNavigation navigation = new(buttons, window, () => window.TryCancelSubView(),
            preferredFocus: () => window.DefaultFocusTarget,
            navigate: direction => !window.HasActiveSurface && window.NavigateWorkspace(direction));
        window.ShowBrightnessSurface();
        Dispatcher.UIThread.RunJobs();
        var close = Assert.IsType<Button>(window.ActiveSurfaceFocusTarget);

        buttons.Press(GamepadButtons.DPadLeft);
        buttons.Press(GamepadButtons.DPadRight);
        buttons.Press(GamepadButtons.DPadUp);
        buttons.Press(GamepadButtons.DPadDown);

        Assert.Same(close, window.FocusManager.GetFocusedElement());
    }

    [AvaloniaFact]
    public void TelemetryRetainsTheFocusedRailButtonAndSelectedSection()
    {
        using FakeDevice device = new();
        using UiFixture fixture = new();
        var window = fixture.Overlay();
        window.AttachDeviceBridge(device);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        var rail = UiFixture.Rail(window, "device.section.overview");
        UiFixture.Click(window, rail);
        device.State = device.State with
        {
            Capabilities = [device.State.Capabilities[0] with { TrailingText = "46 °C" }]
        };
        device.Notify();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(rail, UiFixture.Rail(window, "device.section.overview"));
        Assert.True(rail.IsFocused);
        Assert.Contains("selected", rail.Classes);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text is { IsEffectivelyVisible: true, Text: "46 °C" });
    }
}
