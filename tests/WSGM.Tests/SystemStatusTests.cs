using System.Globalization;
using System.Runtime.InteropServices;
using WSGM.Shell;

namespace WSGM.Tests;

/// <summary>Pure logic of the taskbar status cluster: clock/date formatting,
/// battery interpretation (incl. the GetSystemPowerStatus unknown markers), the
/// WLAN_INTERFACE_INFO_LIST interpretation (all records, not just the first), and
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
    public void WifiListWithZeroInterfacesIsUnknown()
        => Assert.Equal(SystemStatus.WifiState.Unknown, ReadList());

    [Fact]
    public void WifiSingleConnectedInterfaceIsConnected()
        => Assert.Equal(SystemStatus.WifiState.Connected, ReadList(Connected));

    [Fact]
    public void WifiSingleDisconnectedInterfaceIsDisconnected()
        => Assert.Equal(SystemStatus.WifiState.Disconnected, ReadList(NotReady));

    [Fact]
    public void WifiDisconnectedOnboardAdapterDoesNotMaskAConnectedUsbAdapter()
        => Assert.Equal(SystemStatus.WifiState.Connected, ReadList(NotReady, Connected));

    [Fact]
    public void WifiAllInterfacesDisconnectedIsDisconnected()
        => Assert.Equal(SystemStatus.WifiState.Disconnected, ReadList(NotReady, NotReady, NotReady));

    // wlan_interface_state values: 0 = not_ready, 1 = connected.
    private const int NotReady = 0;
    private const int Connected = 1;

    /// <summary>Builds a native WLAN_INTERFACE_INFO_LIST (8-byte header + 532-byte
    /// records with isState at record offset 528) holding the given per-interface
    /// states and runs it through <see cref="SystemStatus.ReadInterfaceList"/>.</summary>
    private static SystemStatus.WifiState ReadList(params int[] states)
    {
        const int HeaderSize = 8;
        const int RecordSize = 532;
        const int StateOffset = 528;
        var size = HeaderSize + (states.Length * RecordSize);
        var list = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, list, size);
            Marshal.WriteInt32(list, states.Length);
            for (var i = 0; i < states.Length; i++)
            {
                Marshal.WriteInt32(list, HeaderSize + (i * RecordSize) + StateOffset, states[i]);
            }
            return SystemStatus.ReadInterfaceList(list);
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
    }

    [Fact]
    public void WifiFlyoutWordingCoversEveryState()
    {
        Assert.Equal("Connected", SystemStatus.DescribeWifi(SystemStatus.WifiState.Connected));
        Assert.Equal("Not connected", SystemStatus.DescribeWifi(SystemStatus.WifiState.Disconnected));
        Assert.Equal("State unavailable", SystemStatus.DescribeWifi(SystemStatus.WifiState.Unknown));
    }
}
