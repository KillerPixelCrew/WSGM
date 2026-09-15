using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class ControllerNavigationTests
{
    [AvaloniaFact]
    public async Task ControllerCardOpensGlyphsAndExplainsUnavailableOutputWithoutIntegration()
    {
        using SimulatedDeviceOverlaySource source = new();
        using FakeDevice device = new()
        {
            SampleSource = source,
            State = source.Snapshot() with
            {
                Visible = false,
                Capabilities = [],
                Controller = null,
                PluginSections = DeviceOverlayBridge.ProjectSections(DeviceSections.IncludePredefined([])),
            },
        };
        using UiFixture fixture = new();
        using PowerSchemeSelection schemes = new(new PowerSchemes(new FakePower()),
            _ => throw new InvalidOperationException("Unexpected power write"));
        await schemes.RefreshAsync();
        var window = fixture.Overlay();
        DeviceGlyphSelection? requested = null;
        device.SelectGlyphs = selection => { requested = selection; return Task.CompletedTask; };
        window.AttachDeviceBridge(device);
        window.AttachPowerSchemes(schemes);
        UiFixture.Click(window, UiFixture.Tab(window, 2));
        var controller = window.GetVisualDescendants().OfType<CardButton>()
            .Single(card => card.IsEffectivelyVisible && card.Title == "Controller");
        UiFixture.Click(window, controller);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CardButton>(),
            card => card.IsEffectivelyVisible && card.Title == "Controller");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text?.Contains("Device integration is off.") is true);
        Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(),
            choice => choice.IsEffectivelyVisible && Equals(choice.Tag, "device.glyph-selection"));
        Assert.Null(requested);
        var glyphs = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(choice => choice.IsEffectivelyVisible && Equals(choice.Tag, "device.glyph-selection"));
        UiFixture.Click(window, glyphs);
        UiFixture.Key(window, Avalonia.Input.Key.Down);
        UiFixture.Key(window, Avalonia.Input.Key.Enter);
        Assert.Equal(DeviceGlyphSelection.NativeSteam, requested);
        UiFixture.Key(window, Avalonia.Input.Key.Escape);
        Assert.Contains(window.GetVisualDescendants().OfType<CardButton>(),
            card => card.IsEffectivelyVisible && card.Title == "Controller");
    }
}
