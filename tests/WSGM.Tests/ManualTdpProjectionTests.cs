using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ManualTdpProjectionTests
{
    [Fact]
    public void UnifiedModeKeepsBoostReadbackButOnlyPrimaryIsWritable()
    {
        DeviceOverlayCapability primary = new("primary", null, DeviceOverlaySection.PowerAndThermals,
            default, "Sustained", "", "20 W", true)
        {
            Role = CapabilityRole.PowerSustainedLimit,
            Writable = true,
        };
        var boost = primary with { CapabilityId = "boost", Role = CapabilityRole.PowerSlowLimit, TrailingText = "30 W" };
        Assert.Equal("TDP", DeviceOverlayBridge.ProjectManualTdp(primary, true).Title);
        var readback = DeviceOverlayBridge.ProjectManualTdp(boost, true);
        Assert.False(readback.Writable);
        Assert.False(readback.CanInvoke);
        Assert.Equal("30 W", readback.TrailingText);
        Assert.Equal(boost, DeviceOverlayBridge.ProjectManualTdp(boost, false));
    }
}
