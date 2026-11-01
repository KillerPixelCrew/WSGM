using WSGM.Core;
using WSGM.Shell;
using OemControlDescriptor = LibHandheld.Contracts.OemControlDescriptor;
using OemControlEvent = LibHandheld.Contracts.OemControlEvent;
using OemControlPlacement = LibHandheld.Contracts.OemControlPlacement;

namespace WSGM.Tests.Core;

public sealed class OemActionPolicyTests
{
    [Fact]
    public void PublicSdk_ExposesPhysicalOemFactsButNoWsgmActionPolicy()
    {
        var exported = typeof(OemControlDescriptor).Assembly.GetExportedTypes();

        Assert.Contains(exported, type => type == typeof(OemControlDescriptor));
        Assert.Contains(exported, type => type == typeof(OemControlEvent));
        Assert.DoesNotContain(exported, type => type.Name is nameof(OemAction) or "OemActionRules");
    }

    [Theory]
    [InlineData(OemAction.VirtualTargetRearButton1)]
    [InlineData(OemAction.VirtualTargetRearButton2)]
    public void VirtualTargetRearButton_RequiresRearPlacement(OemAction action)
    {
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
    [InlineData(OemAction.MouseSecondaryButton)]
    public void WsgmAction_IsAssignableToEitherPhysicalPlacement(OemAction action)
    {
        Assert.True(OemActionRules.IsAssignable(action, OemControlPlacement.Front));
        Assert.True(OemActionRules.IsAssignable(action, OemControlPlacement.Rear));
    }

    [Fact]
    public void RearBinding_RequiresATargetThatExposesRearControls()
    {
        Assert.False(OemActionRules.IsAvailable(
            OemAction.VirtualTargetRearButton1,
            false));
        Assert.True(OemActionRules.IsAvailable(
            OemAction.VirtualTargetRearButton1,
            true));
    }

    [Fact]
    public void RoutingVocabulary_HasNoExecutableOrGeneralRemappingEscapeHatch()
    {
        string[] expected =
        [
            "Disabled",
            "ToggleWsgmOverlay",
            "ToggleSteamQuickAccess",
            "ToggleSteamOverlay",
            "ShowWsgmDevicePage",
            "ToggleWsgmTaskbar",
            "ToggleDesktopGameMode",
            "ToggleOnScreenKeyboard",
            "CyclePerformanceProfile",
            "CyclePerformanceOverlayLevel",
            "VirtualTargetRearButton1",
            "VirtualTargetRearButton2",
            "MouseSecondaryButton"
        ];

        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal),
            Enum.GetNames<OemAction>().OrderBy(name => name, StringComparer.Ordinal));
        Assert.True(OemActionRules.IsVirtualTargetButton(OemAction.VirtualTargetRearButton2));
        Assert.False(OemActionRules.IsVirtualTargetButton(OemAction.ToggleWsgmOverlay));
    }
}
