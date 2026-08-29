using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class OemActionPolicyTests
{
    [Fact]
    public void PublicSdk_ExposesPhysicalOemFactsButNoWsgmActionPolicy()
    {
        Type[] exported = typeof(OemControlDescriptor).Assembly.GetExportedTypes();

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
    public void WsgmAction_IsAssignableToEitherPhysicalPlacement(OemAction action)
    {
        Assert.True(OemActionRules.IsAssignable(action, OemControlPlacement.Front));
        Assert.True(OemActionRules.IsAssignable(action, OemControlPlacement.Rear));
    }

    [Fact]
    public void RearBinding_RequiresATargetThatExposesRearControls()
    {
        bool available = OemActionRules.IsAvailable(
            OemAction.VirtualTargetRearButton1,
            targetHasRearButtons: false,
            out CapabilityReason? reason);

        Assert.False(available);
        Assert.Equal(CapabilityReasonCode.Unsupported, reason!.Code);
        Assert.True(OemActionRules.IsAvailable(
            OemAction.VirtualTargetRearButton1,
            targetHasRearButtons: true,
            out reason));
        Assert.Null(reason);
    }

    [Fact]
    public void RoutingVocabulary_HasNoExecutableOrGeneralRemappingEscapeHatch()
    {
        string[] expected =
        [
            "Disabled",
            "ToggleWsgmOverlay",
            "ToggleSteamQuickAccess",
            "ShowWsgmDevicePage",
            "ToggleWsgmTaskbar",
            "ToggleDesktopGameMode",
            "ToggleOnScreenKeyboard",
            "CyclePerformanceProfile",
            "CyclePerformanceOverlayLevel",
            "VirtualTargetRearButton1",
            "VirtualTargetRearButton2",
        ];

        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal),
            Enum.GetNames<OemAction>().OrderBy(name => name, StringComparer.Ordinal));
        Assert.True(OemActionRules.IsVirtualTargetButton(OemAction.VirtualTargetRearButton2));
        Assert.False(OemActionRules.IsVirtualTargetButton(OemAction.ToggleWsgmOverlay));
    }
}
