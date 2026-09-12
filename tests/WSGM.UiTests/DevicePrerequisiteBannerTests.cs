using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;

namespace WSGM.UiTests;

public sealed class DevicePrerequisiteBannerTests
{
    private static DevicePrerequisiteState State(
        bool package = true,
        bool integration = false,
        bool library = false,
        bool hidHide = false) => new(package, integration, library, hidHide);

    /// <summary>The banner is a child of the Device panel, so that panel's visibility is the gate
    /// and attaching the reader is what decides the banner itself. The panel is shown here because
    /// the tab that normally shows it needs a device coordinator or a power-scheme selection, and
    /// neither is what these tests are about.</summary>
    private static OverlayWindow Device(UiFixture fixture, DevicePrerequisiteSource source)
    {
        OverlayWindow window = fixture.Overlay();
        UiFixture.Named<Control>(window, "PanelDevice").IsVisible = true;
        window.AttachDevicePrerequisites(source);
        return window;
    }

    [AvaloniaFact]
    public void APackageOnAnInstallWithNoControllerSupportIsExplainedOnTheDevicePage()
    {
        using UiFixture fixture = new();
        DevicePrerequisiteSource source = new(() => State(), () => Task.CompletedTask);

        OverlayWindow window = Device(fixture, source);

        Border banner = UiFixture.Named<Border>(window, "DevicePrerequisiteBanner");
        TextBlock detail = UiFixture.Named<TextBlock>(window, "DevicePrerequisiteDetail");
        Assert.True(banner.IsVisible);
        Assert.Contains("Device Integration is switched off", detail.Text!, StringComparison.Ordinal);
        Assert.Contains("Re-run the WSGM setup", detail.Text!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheBannerOffersTheHalfWSGMOwnsAndNeverOffersToInstallTheDriver()
    {
        // INV-020: the runtime never installs a driver. The banner may switch Device Integration
        // on, because that is WSGM's own setting, and must only point at setup for the rest.
        using UiFixture fixture = new();
        bool enabled = false;
        DevicePrerequisiteSource source = new(
            () => State(integration: enabled),
            () => { enabled = true; return Task.CompletedTask; });

        OverlayWindow window = Device(fixture, source);
        Button enable = UiFixture.Named<Button>(window, "DevicePrerequisiteEnable");
        Assert.True(enable.IsVisible);
        Assert.Equal("Enable Device Integration", enable.Content);
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<Button>(),
            button => button.Content is string text && text.Contains("driver", StringComparison.OrdinalIgnoreCase));

        UiFixture.Click(window, enable);

        Assert.True(enabled);
        // The controller half is still missing, so the banner stays and only the button goes.
        Assert.True(UiFixture.Named<Border>(window, "DevicePrerequisiteBanner").IsVisible);
        Assert.False(enable.IsVisible);
    }

    [AvaloniaFact]
    public void AnInstallWithNoDevicePackageShowsNothing()
    {
        using UiFixture fixture = new();
        DevicePrerequisiteSource source = new(
            () => State(package: false), () => Task.CompletedTask);

        OverlayWindow window = Device(fixture, source);

        Assert.False(UiFixture.Named<Border>(window, "DevicePrerequisiteBanner").IsVisible);
    }

    [AvaloniaFact]
    public void ACompleteInstallShowsNothing()
    {
        using UiFixture fixture = new();
        DevicePrerequisiteSource source = new(
            () => State(integration: true, library: true, hidHide: true), () => Task.CompletedTask);

        OverlayWindow window = Device(fixture, source);

        Assert.False(UiFixture.Named<Border>(window, "DevicePrerequisiteBanner").IsVisible);
    }

    [AvaloniaFact]
    public void AReaderThatThrowsLeavesTheOverlayUsable()
    {
        // A banner is not worth failing an overlay open over.
        using UiFixture fixture = new();
        DevicePrerequisiteSource source = new(
            () => throw new InvalidOperationException("the slot is unreadable"),
            () => Task.CompletedTask);

        OverlayWindow window = Device(fixture, source);

        Assert.False(UiFixture.Named<Border>(window, "DevicePrerequisiteBanner").IsVisible);
    }
}
