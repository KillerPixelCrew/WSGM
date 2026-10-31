using WSGM.Shell;
using RadioPower = WindowsDeviceControl.WindowsRadio.Power;

namespace WSGM.Tests.Shell;

public class RadioManagerTests
{
    [Theory]
    [InlineData(RadioPower.Off, "is off")]
    [InlineData(RadioPower.Disabled, "blocked")]
    [InlineData(RadioPower.Absent, "no Wi-Fi adapter")]
    [InlineData(RadioPower.Unknown, "unavailable")]
    public void AnUnusableRadioSaysWhyRatherThanJustOff(RadioPower power, string expected)
    {
        // "Off" for a policy-blocked or missing adapter leaves the user
        // pressing a switch that cannot do anything.
        Assert.Contains(expected, RadioManager.DescribeUnavailable(power, "Wi-Fi"));
    }
}
