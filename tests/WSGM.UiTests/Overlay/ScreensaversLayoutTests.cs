using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class ScreensaversLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1024, 640)]
    public async Task SourcesAndPlaybackOptionsUseTheRealSharedService(int width, int height)
    {
        using var fixture = new UiFixture();
        var images = Path.Combine(fixture.Store.Context.Root, "screensaver-images");
        Directory.CreateDirectory(images);
        File.WriteAllBytes(Path.Combine(images, "one.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII="));
        var config = new ScreensaverConfig
        {
            Sources = [new ScreensaverSourceConfig { Id = "local", Name = "Local photos", Folder = images }]
        };
        await using var service = new ScreensaverService(fixture.Store.Context, () => config, change => change(config), () => false);
        Assert.True(Assert.Single(service.ReadState().Sources).Available);
        var window = fixture.Overlay(width, height, session: new OverlayWindow.SessionState
        {
            Destination = OverlayDestination.System
        });
        window.AttachScreensavers(service);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.Screensavers));
        Dispatcher.UIThread.RunJobs();
        var page = UiFixture.Named<ScreensaversView>(window, "ScreensaversHost");
        Assert.True(page.IsEffectivelyVisible);
        var folder = page.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Tag, "source:local"));
        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", $"screensavers-{width}x{height}");
        Directory.CreateDirectory(directory);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "sources.png"));
        UiFixture.Click(window, folder);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(page.GetVisualDescendants().OfType<Control>(), control => Equals(control.Tag, "source.preview"));
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "options.png"));
        window.AttachScreensavers(null);
    }
}
