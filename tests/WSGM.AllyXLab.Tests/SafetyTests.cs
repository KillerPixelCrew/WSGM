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
        Assert.Throws<InvalidOperationException>(() => Limits.Validate(new(ActionKind.RumbleCalibration, "test", 51)));
        Assert.Throws<InvalidOperationException>(() => Limits.Validate(new(ActionKind.ReadPower, "test", ExpectedAc: 2)));
        Assert.Throws<InvalidOperationException>(() => Limits.Validate(new(ActionKind.Rgb, "test", 3)));
    }

    [Fact]
    public void RumbleBoundaryDoesNotInventAnUnfeltDefault()
    {
        var phase = new RumblePhase([50, 32, 20]);
        phase.Answer(false);
        Assert.True(phase.Complete);
        Assert.Null(phase.LowestFelt);
        Assert.Equal(50, phase.FirstNotFelt);
        Assert.Equal("nothing-felt-at-ceiling", phase.Status);
        Assert.Throws<InvalidOperationException>(() => phase.Answer(true));
    }

    [Fact]
    public void RumbleRequiresAnAnswerToTheFinalPulse()
    {
        var phase = new RumblePhase([50, 32]);
        phase.Answer(true);
        Assert.False(phase.Complete);
        Assert.Equal(32, phase.Current);
        phase.Answer(false);
        Assert.Equal(50, phase.LowestFelt);
        Assert.Equal(32, phase.FirstNotFelt);
        Assert.Equal("boundary-bracketed", phase.Status);
    }

    [Fact]
    public void RumbleAllFeltKeepsTheOpenLowerBoundary()
    {
        var phase = new RumblePhase([20, 10]);
        phase.Answer(true); phase.Answer(true);
        Assert.Equal(10, phase.LowestFelt);
        Assert.Null(phase.FirstNotFelt);
        Assert.Equal("felt-at-lowest-tested-value", phase.Status);
    }

    [Fact]
    public void RumbleCopiesAndValidatesItsSchedule()
    {
        int[] values = [50, 20];
        var phase = new RumblePhase(values); values[0] = 1000;
        Assert.Equal(50, phase.Current);
        Assert.Throws<ArgumentException>(() => new RumblePhase([20, 50]));
        Assert.Throws<ArgumentException>(() => new RumblePhase([]));
    }

    [Fact]
    public void IdentityAdmitsTheOriginalAllyXOnlyWithItsBoardSkuAndFirmware()
    {
        Identity ally = new("ASUSTeK COMPUTER INC.", "ROG Ally X RC72LA_RC72LA", "RC72LA", "test-sku", "test-bios", "test", "Windows");
        Assert.Equal(AllyXModel.RogAllyX, ally.Variant);
        Assert.False((ally with { Model = "ROG Xbox Ally X RC73XA" }).MatchesModel);
        Assert.False((ally with { Board = "RC71L" }).MatchesModel);
        Assert.False((ally with { Board = "RC73XA" }).MatchesModel);
        Assert.False((ally with { Manufacturer = "Another vendor" }).MatchesModel);
        Assert.False((ally with { Bios = "" }).MatchesModel);
        Assert.False((ally with { Sku = "" }).MatchesModel);
    }

    [Fact]
    public void IdentityAdmitsTheXboxAllyXWithoutASkuButNotTheXboxAlly()
    {
        // As inventoried on BIOS RC73XA.317: the system SKU is empty.
        Identity xbox = new("ASUSTeK COMPUTER INC.", "ROG Xbox Ally X RC73XA_RC73XA", "RC73XA", "", "RC73XA.317", "3.14", "Windows");
        Assert.Equal(AllyXModel.XboxAllyX, xbox.Variant);
        Assert.False((xbox with { Model = "ROG Xbox Ally RC73YA_RC73YA", Board = "RC73YA" }).MatchesModel);
        Assert.False((xbox with { Board = "RC72LA" }).MatchesModel);
        Assert.False((xbox with { Model = "ROG Ally X RC72LA_RC72LA" }).MatchesModel);
        Assert.False((xbox with { Manufacturer = "Another vendor" }).MatchesModel);
        Assert.False((xbox with { Bios = "" }).MatchesModel);
    }

    [Fact]
    public void EveryControlIsAskedForOnceAndTheXboxButtonIsIncluded()
    {
        var steps = InputSteps.All();
        Assert.Contains(steps, step => step.Name == "Xbox");
        Assert.Contains(steps, step => step.Name == "Command Center");
        Assert.Contains(steps, step => step.Name == "Rear M1");
        Assert.Equal(steps.Select(step => step.Name).Distinct().Count(), steps.Count);
        Assert.Equal(InputStepKind.Baseline, steps[0].Kind);
        // A press ends its own step, so no step may wait on a hold that nobody asked for.
        Assert.All(steps.Where(step => step.Kind == InputStepKind.Press), step => Assert.Equal(0, step.Seconds));
        Assert.All(steps.Where(step => step.Kind == InputStepKind.Hold), step => Assert.InRange(step.Seconds, 1, 20));
        Assert.Contains(steps, step => step.Kind == InputStepKind.Movement);
    }

    [Fact]
    public void QuietWindowsAllowForSlowerControls()
    {
        Assert.True(InputSteps.QuietMilliseconds(new("Left stick", "", InputStepKind.Press))
            > InputSteps.QuietMilliseconds(new("A", "", InputStepKind.Press)));
        Assert.True(InputSteps.QuietMilliseconds(new("Yaw", "", InputStepKind.Movement))
            > InputSteps.QuietMilliseconds(new("Left stick", "", InputStepKind.Press)));
    }

    [Fact]
    public void HidHideEntriesMatchAcrossBothPathNotations()
    {
        static string? Volumes(string drive) => drive.ToUpperInvariant() switch
        {
            "C:" => @"\Device\HarddiskVolume3",
            "D:" => @"\Device\HarddiskVolume4",
            _ => null,
        };
        string[] stored = [@"\Device\HarddiskVolume3\Tools\AllyXLab.exe"];
        Assert.True(HidHideAccess.Contains(stored, @"C:\Tools\AllyXLab.exe", Volumes));
        Assert.False(HidHideAccess.Contains(stored, @"C:\Tools\Other.exe", Volumes));
        Assert.Equal(@"\Device\HarddiskVolume3\Tools\AllyXLab.exe", HidHideAccess.NormalizePath(@"c:/Tools/AllyXLab.exe", Volumes));
        Assert.Equal(string.Empty, HidHideAccess.NormalizePath("   ", Volumes));
    }

    [Fact]
    public void HidHideEntriesOnAnotherVolumeDoNotMatch()
    {
        static string? Volumes(string drive) => drive.ToUpperInvariant() switch
        {
            "C:" => @"\Device\HarddiskVolume3",
            "D:" => @"\Device\HarddiskVolume4",
            _ => null,
        };
        Assert.False(HidHideAccess.Contains([@"\Device\HarddiskVolume3\Tools\AllyXLab.exe"], @"D:\Tools\AllyXLab.exe", Volumes));
        Assert.False(HidHideAccess.Contains([@"C:\Tools\AllyXLab.exe"], @"D:\Tools\AllyXLab.exe", Volumes));
        Assert.False(HidHideAccess.Contains([@"\Device\HarddiskVolume3\Tools\AllyXLab.exe"], @"E:\Tools\AllyXLab.exe", Volumes));
    }

    [Fact]
    public void ServicesAreNeverStoppedByTheLab()
    {
        SessionLog log = new();
        RunningManager service = new("ASUS app service", "AsusAppService", 4, false, "carries OEM button events");
        Assert.False(Conflicts.Close(service, log));
        Assert.Contains(log.Events, e => e.Kind == "manager-close-refused");
    }

    [Fact]
    public void UnknownMotorRoutesAreRefused()
    {
        SessionLog log = new();
        Assert.Throws<InvalidOperationException>(() => Motors.Open("bluetooth:0", [], log));
        Assert.Throws<InvalidOperationException>(() => Motors.Open("xinput:9", [], log));
        Assert.Throws<InvalidOperationException>(() => Motors.Open("xinput:-1", [], log));
        Assert.Throws<InvalidOperationException>(() => Motors.Open("hid:missing", [], log));
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
