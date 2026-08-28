using WSGM.Overlay;

namespace WSGM.Tests;

public sealed class OverlayNavigationTests
{
    [Fact]
    public void DeviceDestinationIsAbsentUntilItsCapabilitySourceIsVisible()
    {
        OverlayNavigation navigation = new();

        Assert.Equal(
            new[] { OverlayDestination.Home, OverlayDestination.Steam, OverlayDestination.System },
            navigation.VisibleDestinations);

        navigation.SetDeviceVisible(true);

        Assert.Equal(
            new[] { OverlayDestination.Home, OverlayDestination.Steam, OverlayDestination.Device,
                OverlayDestination.System },
            navigation.VisibleDestinations);
    }

    [Fact]
    public void HidingDeviceWhileItIsSelectedReturnsToHomeAndDropsItsPages()
    {
        OverlayNavigation navigation = new();
        navigation.SetDeviceVisible(true);
        navigation.Select(OverlayDestination.Device);

        navigation.SetDeviceVisible(false);

        Assert.Equal(OverlayDestination.Home, navigation.Destination);
        Assert.Equal(OverlayPage.Home, navigation.Page);
        Assert.Equal(1, navigation.Depth);
    }

    [Fact]
    public void NestedStackRejectsAnotherDestinationAndStopsAtItsBound()
    {
        OverlayNavigation navigation = new();
        navigation.Select(OverlayDestination.Steam);

        Assert.False(navigation.Push(OverlayPage.SystemWakeLocks, "wrong.destination"));
        for (int depth = 1; depth < OverlayNavigation.MaximumDepth; depth++)
        {
            Assert.True(navigation.Push(OverlayPage.SteamLibraryTabs, $"steam.row.{depth}"));
        }

        Assert.False(navigation.Push(OverlayPage.SteamArtwork, "one.too.many"));
        Assert.Equal(OverlayNavigation.MaximumDepth, navigation.Depth);
    }

    [Fact]
    public void BackPriorityIsPopupThenDialogThenNestedThenHomeThenOverlay()
    {
        OverlayNavigation navigation = new();
        navigation.Select(OverlayDestination.Steam);
        navigation.Push(OverlayPage.SteamArtwork, "steam.artwork");

        Assert.Equal(OverlayBackAction.ClosePopup, navigation.BackAction(true, true));
        Assert.Equal(OverlayBackAction.CloseDialog, navigation.BackAction(false, true));
        Assert.Equal(OverlayBackAction.LeaveNestedPage, navigation.BackAction(false, false));

        Assert.Equal("steam.artwork", navigation.Pop());
        Assert.Equal(OverlayBackAction.ReturnHome, navigation.BackAction(false, false));

        navigation.Select(OverlayDestination.Home);
        Assert.Equal(OverlayBackAction.CloseOverlay, navigation.BackAction(false, false));
    }

    [Fact]
    public void FocusMemoryRetainsSemanticKeysWithoutControlReferencesAndClampsScroll()
    {
        OverlayFocusMemory memory = new();

        memory.Remember(OverlayDestination.System, "system.shutdown", -50);

        Assert.Equal(
            new OverlayFocusState("system.shutdown", 0),
            memory.Recall(OverlayDestination.System));
        Assert.Equal(
            new OverlayFocusState(null, 0),
            memory.Recall(OverlayDestination.Home));
    }
}
