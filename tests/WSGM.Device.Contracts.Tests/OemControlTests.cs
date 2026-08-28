using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Input;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of the OEM-button exception: what may be reassigned, and the line
/// that keeps it from becoming a general remapper.
/// </summary>
public class OemControlTests
{
    [Theory]
    [InlineData(OemAction.VirtualTargetRearButton1)]
    [InlineData(OemAction.VirtualTargetRearButton2)]
    public void AFrontButton_CannotBeBoundToAVirtualTargetButton(OemAction action)
    {
        // Making a front OEM button a gameplay button is exactly the general remapping that is out of
        // scope. The rear exception exists because rear paddles already are gameplay controls.
        Assert.False(OemActionRules.IsAssignable(action, OemControlPlacement.Front));
        Assert.True(OemActionRules.IsAssignable(action, OemControlPlacement.Rear));
    }

    [Theory]
    [InlineData(OemAction.Disabled)]
    [InlineData(OemAction.ToggleWsgmOverlay)]
    [InlineData(OemAction.ToggleSteamQuickAccess)]
    [InlineData(OemAction.ShowWsgmDevicePage)]
    [InlineData(OemAction.ToggleWsgmTaskbar)]
    [InlineData(OemAction.ToggleDesktopGameMode)]
    [InlineData(OemAction.ToggleOnScreenKeyboard)]
    [InlineData(OemAction.CyclePerformanceProfile)]
    [InlineData(OemAction.CyclePerformanceOverlayLevel)]
    public void EveryWsgmAction_IsAssignableToBothPlacements(OemAction action)
    {
        Assert.True(OemActionRules.IsAssignable(action, OemControlPlacement.Front));
        Assert.True(OemActionRules.IsAssignable(action, OemControlPlacement.Rear));
    }

    [Fact]
    public void ARearBinding_BecomesUnavailableOnATargetWithoutRearControls()
    {
        // Surfaced with a reason rather than silently falling through, so the user can see why their
        // paddle stopped working after switching to an Xbox target.
        bool available = OemActionRules.IsAvailable(
            OemAction.VirtualTargetRearButton1,
            targetHasRearButtons: false,
            out CapabilityReason? reason);

        Assert.False(available);
        Assert.Equal(CapabilityReasonCode.Unsupported, reason!.Code);
    }

    [Fact]
    public void ThatSameBinding_WorksOnATargetWithRearControls()
    {
        Assert.True(OemActionRules.IsAvailable(
            OemAction.VirtualTargetRearButton1, targetHasRearButtons: true, out CapabilityReason? reason));
        Assert.Null(reason);
    }

    [Fact]
    public void AWsgmAction_StaysAvailableRegardlessOfTheTarget()
    {
        Assert.True(OemActionRules.IsAvailable(
            OemAction.ToggleSteamQuickAccess, targetHasRearButtons: false, out _));
    }

    [Fact]
    public void RoutingIsMutuallyExclusive_AnActionIsEitherForwardedOrConsumed()
    {
        // A press is either a rear control the game sees or a WSGM action, never both - otherwise
        // opening the overlay would also fire a paddle in the game behind it.
        Assert.True(OemActionRules.IsVirtualTargetButton(OemAction.VirtualTargetRearButton2));
        Assert.False(OemActionRules.IsVirtualTargetButton(OemAction.ToggleWsgmOverlay));
    }

    [Fact]
    public void TheActionVocabularyHasNoEscapeHatch()
    {
        // The absence of an action that runs something the user supplies - an executable, a script, a
        // text macro, an arbitrary key sequence - is the whole mechanism.
        //
        // Asserted as an exact set rather than by searching for suspicious words: a scan both misses
        // a passthrough named something innocuous and trips over innocent names. Any addition fails
        // here and has to be justified in review.
        string[] expected =
        [
            "Disabled",
            "ToggleWsgmOverlay", "ToggleSteamQuickAccess", "ShowWsgmDevicePage",
            "ToggleWsgmTaskbar", "ToggleDesktopGameMode", "ToggleOnScreenKeyboard",
            "CyclePerformanceProfile", "CyclePerformanceOverlayLevel",
            "VirtualTargetRearButton1", "VirtualTargetRearButton2",
        ];

        Assert.Equal(
            expected.OrderBy(n => n, StringComparer.Ordinal),
            Enum.GetNames<OemAction>().OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void ARearControlDeclaresItsDependenceOnControllerAcquisition()
    {
        // Only the plugin knows this. On the reference handheld the rear paddles ride on the
        // acquisition mode the plugin selects, while the front buttons arrive over a separate vendor
        // event channel and survive controller management being turned off.
        OemControlDescriptor rearPaddle = new()
        {
            ControlId = "oem3",
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "M1" },
            Placement = OemControlPlacement.Rear,
            RequiresControllerAcquisition = true,
        };

        OemControlDescriptor frontButton = new()
        {
            ControlId = "oem2",
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Quick Settings" },
            Placement = OemControlPlacement.Front,
            SupportsLongPress = true,
            RequiresControllerAcquisition = false,
        };

        Assert.True(rearPaddle.RequiresControllerAcquisition);
        Assert.False(frontButton.RequiresControllerAcquisition);
        Assert.True(frontButton.SupportsLongPress);
    }
}
