using WSGM.Core;
using WSGM.Testing;

namespace WSGM.Tests.Settings;

public sealed class SettingsViewModelTests
{
    // Each switch is exercised at its NON-default value in one of the two cases:
    // both default to true, so asserting a true round trip would also pass if the
    // snapshot never read the view model at all.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SettingsSnapshotPersistsLeftAndRightSteamGestureSwitchesIndependently(
        bool left, bool right)
    {
        var viewModel = SettingsTestServices.Model(new AppConfig());
        viewModel.GestureLeftSteamMenu = left;
        viewModel.GestureRightSteamQuickAccess = right;

        var snapshot = viewModel.SnapshotForPreview();

        Assert.Equal(left, snapshot.Gestures.LeftEdgeSteamMenu);
        Assert.Equal(right, snapshot.Gestures.RightEdgeSteamQuickAccess);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SettingsSnapshotPersistsTopAndBottomSheetGestureSwitchesIndependently(
        bool top, bool bottom)
    {
        var viewModel = SettingsTestServices.Model(new AppConfig());
        viewModel.GestureTop = top;
        viewModel.GestureBottom = bottom;

        var snapshot = viewModel.SnapshotForPreview();

        Assert.Equal(top, snapshot.Gestures.TopEdge);
        Assert.Equal(bottom, snapshot.Gestures.BottomEdge);
    }
}
