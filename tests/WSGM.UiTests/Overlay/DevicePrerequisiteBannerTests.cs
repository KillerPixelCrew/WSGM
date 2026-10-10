using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Install;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class DevicePrerequisiteBannerTests
{
    private static DevicePrerequisiteState State(
        bool supported = true,
        bool integration = true,
        bool library = false,
        bool hidHide = false)
    {
        return new DevicePrerequisiteState(supported, integration, library, hidHide, [SetupComponent.ControllerStack]);
    }

    /// <summary>
    ///     The banner is a child of the Device panel, so that panel's visibility is the gate
    ///     and attaching the reader is what decides the banner itself. The panel is shown here because
    ///     the tab that normally shows it needs a device coordinator or a power-scheme selection, and
    ///     neither is what these tests are about.
    /// </summary>
    private static async Task<OverlayWindow> DeviceAsync(UiFixture fixture, DevicePrerequisiteSource source)
    {
        var window = fixture.Overlay();
        UiFixture.Named<Control>(window, "PanelDevice").IsVisible = true;
        window.AttachDevicePrerequisites(source);
        await window.DevicePrerequisitesRefresh;
        return window;
    }

    [AvaloniaFact]
    public async Task AnEnabledSupportedHandheldWithNoControllerSupportIsExplainedOnTheDevicePage()
    {
        using UiFixture fixture = new();
        DevicePrerequisiteSource source = new(() => State(), () => Task.CompletedTask);

        var window = await DeviceAsync(fixture, source);

        var banner = UiFixture.Named<Border>(window, "DevicePrerequisiteBanner");
        var detail = UiFixture.Named<TextBlock>(window, "DevicePrerequisiteDetail");
        Assert.True(banner.IsVisible);
        Assert.Contains("neither the virtual controller library nor the HidHide driver", detail.Text!,
            StringComparison.Ordinal);
        Assert.Contains("Run Repair", detail.Text!, StringComparison.Ordinal);
        Assert.False(UiFixture.Named<Button>(window, "DevicePrerequisiteEnable").IsVisible);
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Content is string text && text.Contains("driver", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public async Task DisabledIntegrationStaysQuietAndOffersNoEnableAction()
    {
        // Declining the optional integration must not nag about its missing dependencies.
        using UiFixture fixture = new();
        var enabled = false;
        DevicePrerequisiteSource source = new(
            () => State(integration: enabled),
            () =>
            {
                enabled = true;
                return Task.CompletedTask;
            });

        var window = await DeviceAsync(fixture, source);
        var enable = UiFixture.Named<Button>(window, "DevicePrerequisiteEnable");
        Assert.False(UiFixture.Named<Border>(window, "DevicePrerequisiteBanner").IsVisible);
        Assert.False(enable.IsVisible);
        Assert.False(enabled);
    }

    [AvaloniaFact]
    public async Task AnUnsupportedHandheldShowsNothing()
    {
        using UiFixture fixture = new();
        DevicePrerequisiteSource source = new(
            () => State(false), () => Task.CompletedTask);

        var window = await DeviceAsync(fixture, source);

        Assert.False(UiFixture.Named<Border>(window, "DevicePrerequisiteBanner").IsVisible);
    }

    [AvaloniaFact]
    public async Task ACompleteInstallShowsNothing()
    {
        using UiFixture fixture = new();
        DevicePrerequisiteSource source = new(
            () => State(integration: true, library: true, hidHide: true), () => Task.CompletedTask);

        var window = await DeviceAsync(fixture, source);

        Assert.False(UiFixture.Named<Border>(window, "DevicePrerequisiteBanner").IsVisible);
    }

    [AvaloniaFact]
    public async Task AReaderThatThrowsLeavesTheOverlayUsable()
    {
        // A banner is not worth failing an overlay open over.
        using UiFixture fixture = new();
        DevicePrerequisiteSource source = new(
            () => throw new InvalidOperationException("the slot is unreadable"),
            () => Task.CompletedTask);

        var window = await DeviceAsync(fixture, source);

        Assert.False(UiFixture.Named<Border>(window, "DevicePrerequisiteBanner").IsVisible);
    }
}
