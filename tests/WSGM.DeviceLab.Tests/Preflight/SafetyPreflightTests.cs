using WSGM.DeviceLab.Preflight;

namespace WSGM.Device.Tests;

public sealed class SafetyPreflightTests
{
    [Fact]
    public void AttendedHardwareAction_RequiresImmediateConfirmationAndNoProductionOwner()
    {
        DeviceLabOperationRequirements requirements = new()
        {
            OperationId = "plugin.hardware-test",
            ResourceId = "wsgm.device.synthetic.dock-x1",
            Access = DeviceLabOperationAccess.AttendedPluginAction,
            ExactDeviceMatched = true,
            RequiresElevation = true
        };
        DeviceLabSafetySnapshot snapshot = new()
        {
            OwnerDiscovery = DeviceOwnerDiscoveryState.Absent,
            IsElevated = true,
            IsUserInteractive = true,
            IsContinuousIntegration = false,
            AttendedActionConfirmed = false
        };

        var unconfirmed = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            snapshot);
        var confirmed = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            snapshot with { AttendedActionConfirmed = true });
        var owned = DeviceLabSafetyPreflight.Evaluate(
            requirements,
            snapshot with
            {
                AttendedActionConfirmed = true,
                OwnerDiscovery = DeviceOwnerDiscoveryState.Present
            });

        Assert.Equal(DeviceLabAccessRoute.None, unconfirmed.Route);
        Assert.Contains(unconfirmed.Checks, check => check.Code == "attended.confirmation");
        Assert.Equal(DeviceLabAccessRoute.DirectAttended, confirmed.Route);
        Assert.Equal(DeviceLabAccessRoute.None, owned.Route);
        Assert.Contains(owned.Checks, check => check.Code == "owner.active");
    }
}
