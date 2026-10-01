using System.Reflection;
using System.Runtime.InteropServices;
using WSGM.Device.Sdk.Windows;

namespace WSGM.Device.Sdk.Tests.Windows;

public sealed class HidDevicesTests
{
    [Theory]
    [InlineData("UsagePage", 0)]
    [InlineData("ReportId", 2)]
    [InlineData("LinkCollection", 6)]
    [InlineData("IsRange", 12)]
    [InlineData("IsAbsolute", 15)]
    [InlineData("ButtonReportCount", 16)]
    [InlineData("HasNull", 16)]
    [InlineData("BitSize", 18)]
    [InlineData("ValueReportCount", 20)]
    [InlineData("UnitsExponent", 32)]
    [InlineData("Units", 36)]
    [InlineData("LogicalMin", 40)]
    [InlineData("LogicalMax", 44)]
    [InlineData("PhysicalMin", 48)]
    [InlineData("PhysicalMax", 52)]
    [InlineData("UsageMin", 56)]
    [InlineData("UsageMax", 58)]
    public void CapabilityEntryMatchesTheHidpiLayout(string field, int offset)
    {
        // HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS are 72 bytes on x86 and x64; Inspect reads both through one
        // entry, so a wrong offset would report another field's bytes as the usage or the limits.
        var entry = Assert.IsType<Type>(
            typeof(HidDevices).GetNestedType("HidpCapsEntry", BindingFlags.NonPublic), false);

        Assert.Equal(72, Marshal.SizeOf(entry));
        Assert.Equal(offset, Marshal.OffsetOf(entry, field).ToInt32());
    }
}
