using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using WSGM.Core;
using WSGM.Overlay;
using WSGM.Shell;
using WSGM.Testing;
using WSGM.UiTests.Infrastructure;

namespace WSGM.UiTests.Overlay;

public sealed class SteamBuildNoticeTests
{
    [AvaloniaTheory]
    [InlineData(980, 640)]
    [InlineData(1280, 800)]
    public void AnUncheckedSteamBuildLeadsTheControlsPaneWithADismissButton(int width, int height)
    {
        using UiFixture fixture = new();
        var window = fixture.Overlay(width, height);
        var notice = UiFixture.Named<FAInfoBar>(window, "SteamBuildNotice");
        Assert.False(notice.IsOpen);

        var text = SteamClientBuildNotice.Describe(new SteamClientBuild("publicbeta", "1799999999", null));
        ((OverlayViewModel)window.DataContext!).SteamBuildNoticeText = text;
        Dispatcher.UIThread.RunJobs();

        Assert.True(notice.IsOpen);
        Assert.Equal(text, notice.Message);
        var dismiss = notice.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content is "Dismiss");
        Assert.True(dismiss.IsEffectivelyVisible);
        Assert.True(dismiss.Bounds.Width > 0);
        var directory = Path.Combine(RepositoryFiles.Root, "TestResults", "ui", $"steam-build-notice-{width}");
        Directory.CreateDirectory(directory);
        DevicePageCaptureTests.Capture(window, Path.Combine(directory, "viewport.png"));

        var dismissed = 0;
        window.SteamBuildNoticeDismissed += () => dismissed++;
        UiFixture.Click(window, dismiss);
        Assert.Equal(1, dismissed);
        // Keyboard focus reaches it too: Enter on the focused button dismisses as the pointer does.
        dismiss.Focus();
        UiFixture.Key(window, Key.Enter);
        Assert.Equal(2, dismissed);

        ((OverlayViewModel)window.DataContext!).SteamBuildNoticeText = "";
        Dispatcher.UIThread.RunJobs();
        Assert.False(notice.IsOpen);
    }
}
