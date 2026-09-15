using Xunit;

namespace WSGM.AllyXLab.Tests;

public sealed class SafetyTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(26)]
    [InlineData(int.MaxValue)]
    public void RejectsTdpOutsideAttendedEnvelope(int watts) =>
        Assert.Throws<InvalidOperationException>(() => Limits.Validate(new(ActionKind.Tdp, "test", watts)));

    [Fact]
    public void RejectsUnexpectedCommandAndUnboundedCapture()
    {
        Assert.Throws<InvalidOperationException>(() => Limits.Validate(new((ActionKind)999, "test")));
        Assert.Throws<InvalidOperationException>(() => Limits.Validate(new(ActionKind.Capture, "test", Seconds: 21)));
        Assert.Throws<InvalidOperationException>(() => Limits.Validate(new(ActionKind.Rumble, "test", 51)));
        Assert.Throws<InvalidOperationException>(() => Limits.Validate(new(ActionKind.Rumble, "test", 10, PulseMilliseconds: 2001)));
        Assert.Throws<InvalidOperationException>(() => Limits.Validate(new(ActionKind.Rgb, "test", 3)));
    }

    [Fact]
    public void IdentityDoesNotAdmitOriginalOrXboxAllyOrMissingFirmware()
    {
        Identity ally = new("ASUSTeK COMPUTER INC.", "ROG Ally X RC72LA_RC72LA", "RC72LA", "test-sku", "test-bios", "test", "Windows");
        Assert.True(ally.MatchesModel);
        Assert.False((ally with { Model = "ROG Xbox Ally X RC73XA" }).MatchesModel);
        Assert.False((ally with { Board = "RC71L" }).MatchesModel);
        Assert.False((ally with { Manufacturer = "Another vendor" }).MatchesModel);
        Assert.False((ally with { Bios = "" }).MatchesModel);
        Assert.False((ally with { Sku = "" }).MatchesModel);
    }

    [Fact]
    public void RejectsMissingOrMalformedFanRestorationData()
    {
        byte[] curve = [30, 40, 50, 60, 70, 80, 90, 100, 5, 10, 20, 30, 40, 50, 60, 70];
        Assert.True(AsusControl.ValidCurve(curve));
        Assert.False(AsusControl.ValidCurve(new byte[16]));
        Assert.False(AsusControl.ValidCurve(curve[..15]));
        byte[] wrongOrder = (byte[])curve.Clone(); wrongOrder[4] = 35;
        Assert.False(AsusControl.ValidCurve(wrongOrder));
        byte[] badDuty = (byte[])curve.Clone(); badDuty[15] = 255;
        Assert.False(AsusControl.ValidCurve(badDuty));
    }

    [Fact]
    public void InterfaceGateDistinguishesVendorControlFromMotorOutput()
    {
        HidEndpoint vendor = new("private-path", 0x0B05, 0x1B4C, 1, 0xFF31, 0x80, 64, 64, 64);
        Assert.True(vendor.Vendor);
        Assert.False(vendor.Rumble);
        Assert.False((vendor with { Pid = 0x1ABE }).Vendor);
        Assert.True((vendor with { Page = 1, Usage = 5 }).Rumble);
        Assert.DoesNotContain("private-path", vendor.Id);
    }
}
