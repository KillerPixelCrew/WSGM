using WindowsDeviceControl;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class GpuDriverUpdatePolicyTests
{
    [Theory]
    [InlineData(false, 1, 128, 255, false)]
    [InlineData(true, 0, 1, 90, false)]
    [InlineData(true, 255, 1, 90, false)]
    [InlineData(true, 1, 255, 90, false)]
    [InlineData(true, 1, 1, 255, false)]
    [InlineData(true, 1, 1, 29, false)]
    [InlineData(true, 1, 1, 30, true)]
    [InlineData(true, 1, 128, 255, true)]
    public void InstallationRequiresKnownAcAndSafeBattery(bool known, byte ac, byte flags, byte percent, bool accepted)
    {
        var power = new WindowsPowerStatus(ac, flags, percent, 0, uint.MaxValue, uint.MaxValue);
        Assert.Equal(accepted, GpuDriverUpdateService.PowerRefusal(known, power) is null);
    }
}
