using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Device.Sdk.Input;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceOemActionRouterTests
{
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
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = "Armoury Crate" },
            Placement = placement,
            CompanionApplication = companion
        };
    }
}
