using WSGM.Overlay;

namespace WSGM.Tests.Overlay;

public sealed class OverlayViewModelTests
{
    [Fact]
    public void OverlayViewModelRecomputesDerivedTextAndRaisesNotifications()
    {
        var viewModel = new OverlayViewModel();
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        viewModel.HomeAppName = "Steam";
        viewModel.HomeAppAlive = true;
        viewModel.ExplorerRunning = true;
        viewModel.WarningText = "Steam is unavailable";
        viewModel.ConfirmingCloseLauncher = true;

        Assert.Equal("Back to Game Mode", viewModel.DesktopButtonText);
        Assert.Equal("Focus Steam", viewModel.HomeAppButtonText);
        Assert.Equal("Really?", viewModel.CloseLauncherText);
        Assert.True(viewModel.HasWarning);
        Assert.Contains(nameof(OverlayViewModel.HomeAppButtonText), changed);
        Assert.Contains(nameof(OverlayViewModel.DesktopButtonText), changed);
        Assert.Contains(nameof(OverlayViewModel.HasWarning), changed);
        Assert.Contains(nameof(OverlayViewModel.CloseLauncherText), changed);
    }
}
