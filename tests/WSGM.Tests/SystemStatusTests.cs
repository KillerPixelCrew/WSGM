using System.Globalization;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>Pure logic of the taskbar status cluster: clock/date formatting,
/// battery interpretation (incl. the GetSystemPowerStatus unknown markers), and
/// the Wi-Fi state wording shown in the button flyout.</summary>
public sealed class SystemStatusTests
{
    [Fact]
    public void ClockFormatsAsTwentyFourHourHoursAndMinutes()
        => Assert.Equal("21:37", SystemStatus.FormatClock(new DateTime(2026, 8, 8, 21, 37, 45)));

    [Fact]
    public void ClockZeroPadsTheEarlyHours()
        => Assert.Equal("09:05", SystemStatus.FormatClock(new DateTime(2026, 8, 8, 9, 5, 0)));

    [Fact]
    public void DateFormatsAsDayNameDayNumberAndMonth()
        => Assert.Equal(
            "Sat 08 Aug",
            SystemStatus.FormatDate(new DateTime(2026, 8, 8), CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(true, (byte)1, (byte)87, true, 87, "87%")] // healthy battery
    [InlineData(true, (byte)8, (byte)100, true, 100, "100%")] // charging flag, full
    [InlineData(true, (byte)128, (byte)0, false, 0, "")] // 128 = no system battery
    [InlineData(true, (byte)255, (byte)50, false, 0, "")] // 255 flag = unknown
    [InlineData(true, (byte)1, (byte)255, false, 0, "")] // 255 percent = unknown
    [InlineData(false, (byte)1, (byte)50, false, 0, "")] // API call failed
    public void BatteryIndicatorHidesOnEveryUnknownMarkerAndShowsThePercentOtherwise(
        bool ok, byte flag, byte percent, bool expectedHas, int expectedPercent, string expectedText)
    {
        var (hasBattery, batteryPercent, text) = SystemStatus.InterpretBattery(ok, flag, percent);
        Assert.Equal(expectedHas, hasBattery);
        Assert.Equal(expectedPercent, batteryPercent);
        Assert.Equal(expectedText, text);
    }

    [Fact]
    public void WifiFlyoutWordingCoversEveryState()
    {
        Assert.Equal("Connected", SystemStatus.DescribeWifi(SystemStatus.WifiState.Connected));
        Assert.Equal("Not connected", SystemStatus.DescribeWifi(SystemStatus.WifiState.Disconnected));
        Assert.Equal("State unavailable", SystemStatus.DescribeWifi(SystemStatus.WifiState.Unknown));
    }
}
