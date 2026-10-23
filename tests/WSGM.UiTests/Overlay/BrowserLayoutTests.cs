using Avalonia;
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

/// <summary>Real Browser chrome and local keyboard at handheld sizes, without a native renderer.</summary>
public sealed class BrowserLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1280, 720)]
    [InlineData(1024, 640)]
    public void BrowserChromeAndKeyboardRemainReachable(int width, int height)
    {
        using var fixture = new UiFixture();
        using var session = new BrowserSession(fixture.Store.Context, new BrowserOptions(),
            options => options.Normalize(), () => new BrowserGameContext("220", "Example Game"));
        var window = fixture.Overlay(width, height, session: new OverlayWindow.SessionState
        {
            Destination = OverlayDestination.System
        });
        window.AttachBrowserSession(session);
        Assert.Null(session.HelperProcessId);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.SystemBrowser));
        Dispatcher.UIThread.RunJobs();
        var page = UiFixture.Named<BrowserView>(window, "BrowserHost");
        Assert.True(page.IsEffectivelyVisible);
        Assert.Null(session.HelperProcessId);
        Assert.Equal(BrowserLifecycle.NotCreated, session.Lifecycle);
        var buttons = page.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.Contains(buttons, button => Equals(button.Content, "Search Example Game…"));
        var keyboard = Assert.Single(buttons, button => Equals(button.Content, "Keyboard"));
        var address = buttons.First(button => button.Content is TextBlock);
        address.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var viewport = Assert.Single(page.GetVisualDescendants().OfType<BrowserViewport>());
        var viewportFrame = Assert.IsType<Grid>(viewport.GetVisualParent());
        var scroller = UiFixture.Named<ScrollViewer>(window, "ContentScroller");
        var origin = viewportFrame.TranslatePoint(default, scroller)!.Value;
        Assert.True(viewportFrame.Bounds.Height >= 240);
        Assert.InRange(origin.Y, 0, scroller.Viewport.Height - viewportFrame.Bounds.Height + 1);
        Assert.False(viewport.IsVisible);
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text == "Enter an address or search above to open a page.");
        var directory = CaptureDirectory(width, height);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "chrome.png"));
        UiFixture.Click(window, keyboard);
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.HasActiveSurface);
        Assert.False(viewport.IsVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text == "Type into the selected web field");
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "keyboard.png"));
        Assert.Null(session.HelperProcessId);
    }

    [AvaloniaTheory]
    [InlineData(1280, 800)]
    [InlineData(1024, 640)]
    public void SettingsMemoryChoiceRemainsReachable(int width, int height)
    {
        using var fixture = new UiFixture();
        var window = fixture.Settings(width, height);
        UiFixture.Click(window, UiFixture.Tab(window, 5));
        Dispatcher.UIThread.RunJobs();
        var page = UiFixture.Named<Control>(window, "PageQuickAccess");
        var memory = Assert.Single(page.GetVisualDescendants().OfType<ComboBox>(),
            choice => Equals(choice.ItemsSource, ((WSGM.Settings.SettingsViewModel)window.DataContext!).BrowserMemoryBehaviors));
        memory.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(memory.IsEffectivelyVisible);
        Assert.True(memory.IsEffectivelyEnabled);
        DevicePageCaptureTests.Capture(window, Path.Combine(CaptureDirectory(width, height), "settings.png"));
    }

    private static string CaptureDirectory(int width, int height)
    {
        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", $"browser-{width}x{height}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
