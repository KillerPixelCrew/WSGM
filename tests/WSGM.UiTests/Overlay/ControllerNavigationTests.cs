using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Fakes;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class ControllerNavigationTests
{
    [AvaloniaFact]
    public async Task ControllerCardOpensGlyphsAndExplainsUnavailableOutputWithoutIntegration()
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
        var controller = window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card is { IsEffectivelyVisible: true, Title: "Controller" });
        UiFixture.Click(window, controller);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CardButton>(),
            card => card is { IsEffectivelyVisible: true, Title: "Controller" });
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
        Assert.Contains(window.GetVisualDescendants().OfType<CardButton>(),
            card => card is { IsEffectivelyVisible: true, Title: "Controller" });
    }
}
