using WSGM.Core;
using WSGM.Shell;
using CapabilityDisplay = LibHandheld.Contracts.CapabilityDisplay;
using DisplayKey = LibHandheld.Contracts.DisplayKey;
using OemControlDefaultActionHint = LibHandheld.Contracts.OemControlDefaultActionHint;
using OemControlDescriptor = LibHandheld.Contracts.OemControlDescriptor;
using OemControlEdge = LibHandheld.Contracts.OemControlEdge;
using OemControlEvent = LibHandheld.Contracts.OemControlEvent;
using OemControlPlacement = LibHandheld.Contracts.OemControlPlacement;
using OemPressKind = LibHandheld.Contracts.OemPressKind;

namespace WSGM.Tests.Shell;

public sealed class DeviceOemActionRouterTests
{
    [Fact]
    public void TabletTouchpadGestureDefaultsToTheSecondaryMouseButton()
    {
        Assert.Equal(OemAction.MouseSecondaryButton, DeviceOemActionRouter.DefaultAction(TouchpadControl()));
    }

    [Fact]
    public void TouchpadPressAndReleaseStayOrderedWithoutUiServices()
    {
        var edges = new List<bool>();
        using var router = Router(edges);
        router.OnEvent(Event(OemControlEdge.Pressed, "one"));
        router.OnEvent(Event(OemControlEdge.Pressed, "one"));
        router.OnEvent(Event(OemControlEdge.Released, "one"));
        Assert.Equal(new[] { true, false }, edges);
    }

    [Fact]
    public void ExplicitDisabledAssignmentWinsOverTheTouchpadDefault()
    {
        var edges = new List<bool>();
        using var router = Router(edges);
        router.UpdateConfiguration(
            [new DeviceOemAssignment { ControlId = "touchpad-secondary-click", Action = OemAction.Disabled }],
            false, ManagedControllerTarget.Xbox360);
        router.OnEvent(Event(OemControlEdge.Pressed, "one"));
        router.OnEvent(Event(OemControlEdge.Released, "one"));
        Assert.Empty(edges);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("detach")]
    [InlineData("dispose")]
    [InlineData("configuration")]
    [InlineData("controls")]
    public void EveryOwnershipResetReleasesAHeldSyntheticButton(string transition)
    {
        var edges = new List<bool>();
        using var router = Router(edges);
        router.OnEvent(Event(OemControlEdge.Pressed, "one"));
        switch (transition)
        {
            case "reset": router.Reset(); break;
            case "detach": router.Detach(); break;
            case "dispose": router.Dispose(); break;
            case "configuration": router.UpdateConfiguration([], false, ManagedControllerTarget.Xbox360); break;
            case "controls": router.OnControls([]); break;
        }

        router.OnEvent(Event(OemControlEdge.Released, "one"));
        Assert.Equal(new[] { true, false }, edges);
    }

    [Fact]
    public void AnOldReleaseCannotEndANewerAdmittedPress()
    {
        var edges = new List<bool>();
        using var router = Router(edges);
        router.OnEvent(Event(OemControlEdge.Pressed, "old"));
        router.OnEvent(Event(OemControlEdge.Pressed, "new"));
        router.OnEvent(Event(OemControlEdge.Released, "old"));
        Assert.Equal(new[] { true }, edges);
        router.OnEvent(Event(OemControlEdge.Released, "new"));
        Assert.Equal(new[] { true, false }, edges);
    }

    [Fact]
    public void MultipleAssignedControlsShareOneHeldButton()
    {
        var edges = new List<bool>();
        using var router = Router(edges);
        var rear = Control(OemControlPlacement.Rear, false) with { ControlId = "rear" };
        router.OnControls([TouchpadControl(), rear]);
        router.UpdateConfiguration(
            [new DeviceOemAssignment { ControlId = "rear", Action = OemAction.MouseSecondaryButton }],
            false, ManagedControllerTarget.Xbox360);
        router.OnEvent(Event(OemControlEdge.Pressed, "touch"));
        router.OnEvent(Event(OemControlEdge.Pressed, "rear", "rear"));
        router.OnEvent(Event(OemControlEdge.Released, "touch"));
        Assert.Equal(new[] { true }, edges);
        router.OnEvent(Event(OemControlEdge.Released, "rear", "rear"));
        Assert.Equal(new[] { true, false }, edges);
    }

    [Fact]
    public void FailedDownIsImmediatelyReleasedAndAnUnconfirmedUpRemainsOwnedForReset()
    {
        var edges = new List<bool>();
        var allowRelease = false;
        using var router = new DeviceOemActionRouter(down =>
        {
            edges.Add(down);
            return !down && allowRelease;
        });
        router.OnControls([TouchpadControl()]);
        router.OnEvent(Event(OemControlEdge.Pressed, "one"));
        Assert.Equal(new[] { true, false }, edges);
        allowRelease = true;
        router.Reset();
        Assert.Equal(new[] { true, false, false }, edges);
    }

    [Fact]
    public async Task ExistingCompanionActionStillRunsOnlyOnPress()
    {
        var invoked = 0;
        using var router =
            new DeviceOemActionRouter(_ => throw new InvalidOperationException("No mouse edge expected"));
        router.OnControls([Control(OemControlPlacement.Front, true)]);
        router.ConfigureActions(Actions(_ =>
        {
            Interlocked.Increment(ref invoked);
            return Task.FromResult(true);
        }));
        router.OnEvent(Event(OemControlEdge.Pressed, "one", "armoury-crate"));
        router.OnEvent(Event(OemControlEdge.Released, "one", "armoury-crate"));
        await router.Completion;
        Assert.Equal(1, invoked);
    }

    [Fact]
    public void UnassignedCompanionButtonOpensTheOverlay()
    {
        Assert.Equal(OemAction.ToggleWsgmOverlay,
            DeviceOemActionRouter.DefaultAction(Control(OemControlPlacement.Front, true)));
    }

    [Theory]
    [InlineData(OemControlPlacement.Front, false)]
    [InlineData(OemControlPlacement.Rear, false)]
    [InlineData(OemControlPlacement.Rear, true)]
    public void OtherUnassignedControlsDoNothing(OemControlPlacement placement, bool companion)
    {
        Assert.Equal(OemAction.Disabled, DeviceOemActionRouter.DefaultAction(Control(placement, companion)));
    }

    private static OemControlDescriptor Control(OemControlPlacement placement, bool companion)
    {
        return new OemControlDescriptor
        {
            ControlId = "armoury-crate",
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "Armoury Crate" },
            Placement = placement,
            DefaultActionHint = companion
                ? OemControlDefaultActionHint.CompanionApplication
                : OemControlDefaultActionHint.None
        };
    }

    private static DeviceOemActionRouter Router(List<bool> edges)
    {
        var router = new DeviceOemActionRouter(down =>
        {
            edges.Add(down);
            return true;
        });
        router.OnControls([TouchpadControl()]);
        return router;
    }

    private static OemControlDescriptor TouchpadControl()
    {
        return new OemControlDescriptor
        {
            ControlId = "touchpad-secondary-click",
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "Touchpad right click" },
            Placement = OemControlPlacement.Front,
            DefaultActionHint = OemControlDefaultActionHint.MouseSecondaryButton
        };
    }

    private static OemControlEvent Event(OemControlEdge edge, string pressId,
        string control = "touchpad-secondary-click")
    {
        return new OemControlEvent(control, OemPressKind.Short, DateTimeOffset.UtcNow, pressId, edge);
    }

    private static DeviceOemActionServices Actions(Func<CancellationToken, Task<bool>> overlay)
    {
        return new DeviceOemActionServices
        {
            ToggleOverlayAsync = overlay, ToggleSteamQuickAccessAsync = _ => Task.FromResult(true),
            ToggleSteamOverlayAsync = _ => Task.FromResult(true), ToggleDevicePageAsync = _ => Task.FromResult(true),
            ToggleOpenAppsAsync = _ => Task.FromResult(true), ToggleDesktopGameModeAsync = _ => Task.FromResult(true),
            ToggleOnScreenKeyboardAsync = _ => Task.FromResult(true),
            CyclePerformanceProfileAsync = _ => Task.FromResult(true),
            CyclePerformanceOverlayLevelAsync = _ => Task.FromResult(true),
            SetRearButtonAsync = (_, _) => Task.FromResult(true)
        };
    }
}
