using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Tests;

public sealed class EdidModesTests
{
    // The reference MSI Claw 8 AI+ A2VM's internal panel, read from
    // HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\CSW0801\...\Device Parameters\EDID on 2026-08-30.
    // Windows enumerates 30/48/60/75/100/120 for this panel and the driver accepts all six; the
    // panel itself advertises only two, which is the distinction these tests exist to protect.
    // The base block is complete and verbatim; the trailing zero padding of the second block is
    // truncated, since nothing below reads past byte 127.
    private const string ClawEdid =
        "00ffffffffffff000e7701080000000000220104a5110b7803b241a5544c9e240d4e5300000001010101010101"
        + "0101010101010101013e7b80a070b040403020660cac6b000000189f3d80a070b040403020660cac6b0000001"
        + "8000000fd001e78989820010a202020202020000000fc00504e383030375142312d320a2001017020790200810"
        + "014741a000003011e7800000000000078000000008000000000000000000000000000000000000000000000000"
        + "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
        + "000000000000000000000000000000009ea2";

    [Fact]
    public void ReadAdvertisedRefreshRates_TheReferencePanel_ReportsOnlyWhatItActuallyAdvertises()
    {
        IReadOnlyList<int> rates = EdidModes.ReadAdvertisedRefreshRates(Bytes(ClawEdid));

        // Both detailed timings are 1920x1200: 315.50 MHz and 157.75 MHz over a 2080x1264 total.
        Assert.Equal([60, 120], rates);
    }

    [Fact]
    public void ReadAdvertisedRefreshRates_DoesNotReportTheDriverSynthesizedRates()
    {
        IReadOnlyList<int> rates = EdidModes.ReadAdvertisedRefreshRates(Bytes(ClawEdid));

        // Windows enumerates and the driver accepts all of these; the panel advertises none of them.
        Assert.DoesNotContain(30, rates);
        Assert.DoesNotContain(48, rates);
        Assert.DoesNotContain(75, rates);
        Assert.DoesNotContain(100, rates);
    }

    [Fact]
    public void ReadVerticalRange_TheReferencePanel_ReportsTheAdaptiveSyncBand()
    {
        (int minimum, int maximum)? range = EdidModes.ReadVerticalRange(Bytes(ClawEdid));

        // 0x1e-0x78 in the display-range-limits descriptor, and the same 30-120 Hz band Arc Sync
        // independently reports for this panel.
        Assert.Equal((30, 120), range);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00ffffffffffff00")]
    public void ReadAdvertisedRefreshRates_ShortOrMissingData_ReportsNothingRatherThanGuessing(
        string? hex)
    {
        Assert.Empty(EdidModes.ReadAdvertisedRefreshRates(hex is null ? null : Bytes(hex)));
    }

    [Fact]
    public void ReadAdvertisedRefreshRates_WrongHeader_IsRefused()
    {
        byte[] corrupt = Bytes(ClawEdid);
        corrupt[1] = 0x00;

        Assert.Empty(EdidModes.ReadAdvertisedRefreshRates(corrupt));
    }

    [Fact]
    public void ReadVerticalRange_PanelWithoutARangeDescriptor_ReportsNone()
    {
        byte[] noRange = Bytes(ClawEdid);

        // Blank the descriptor's type byte so it no longer reads as display range limits.
        noRange[54 + (2 * 18) + 3] = 0x00;

        Assert.Null(EdidModes.ReadVerticalRange(noRange));
    }

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);
}
