using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WSGM.Controls;
using WSGM.Overlay;
using WSGM.UiTests.Infrastructure;
using WSGM.UiTests.Visual;

namespace WSGM.UiTests.Overlay;

public sealed class EmulatorManagerViewTests
{
    [AvaloniaFact]
    public void InstalledAndAvailableSeparateActualInstallationsAndBackPreservesTheList()
    {
        using var fixture = new UiFixture();
        var window = PreviewTools.Create(fixture);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.EmulatorManager));
        Assert.Contains(Visible(window), button => button.Title == "PCSX2" && button.TrailingText == "Update v2.4.1");
        Assert.DoesNotContain(Visible(window), button => button.Title == "Eden");
        Click(window, "Available 2");
        Assert.Contains(Visible(window), button => button.Title == "Eden");
        Assert.DoesNotContain(Visible(window), button => button.Title == "PCSX2");
        Click(window, "Eden");
        Assert.Contains(Visible(window), button => button.Title == "Install stable");
        UiFixture.Key(window, Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Visible(window), button => button.Title == "Eden");
        Assert.DoesNotContain(Visible(window), button => button.Title == "Install stable");
    }

    [AvaloniaFact]
    public async Task BusyInstallationKeepsCancellationReachableAndDisablesPackageWrites()
    {
        using var fixture = new UiFixture();
        var window = PreviewTools.Create(fixture);
        var source = PreviewEmulators.Create();
        source.State = source.State with { Busy = true, Status = "Extracting RPCS3" };
        UiFixture.Named<EmulatorManagerView>(window, "EmulatorManagerHost").Attach(source);
        UiFixture.Click(window, UiFixture.Rail(window, OverlayPage.EmulatorManager));
        Click(window, "Available 2");
        Click(window, "Eden");
        Assert.False(Visible(window).Single(button => button.Title == "Install stable").IsEnabled);
        Assert.False(Visible(window).Single(button => button.Title == "Use an existing installation").IsEnabled);
        var stop = Visible(window).Single(button => button.Title == "Stop operation");
        Assert.True(stop.IsEnabled);
        UiFixture.Click(window, stop);
        Assert.Equal("CancelAsync", await source.CommandCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("CancelAsync", source.Commands);
        Assert.DoesNotContain("InstallEmulatorAsync", source.Commands);
    }

    private static ActionButton[] Visible(OverlayWindow window)
    {
        return window.GetVisualDescendants().OfType<ActionButton>().Where(button => button.IsEffectivelyVisible)
            .ToArray();
    }

    private static void Click(OverlayWindow window, string title)
    {
        UiFixture.Click(window, Visible(window).Single(button => button.Title == title));
        Dispatcher.UIThread.RunJobs();
    }
}
