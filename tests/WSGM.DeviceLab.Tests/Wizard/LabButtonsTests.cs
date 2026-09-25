using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabButtonsTests
{
    private static DeviceKnowledgeRecord Record(string id)
    {
        return DeviceKnowledgeBase.Default.Records.Single(record => record.Id == id);
    }

    [Fact]
    public void Plan_AsksForEveryControlOnceAndIncludesTheXboxButton()
    {
        var plan = LabButtonPlan.For(null);

        Assert.Equal(plan.Count, plan.Select(control => control.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(plan, control => control.Id == "guide");
        Assert.Contains(plan, control => control.Id == "guide-hold");
        Assert.Equal("channel-check", plan[0].Id);
        Assert.All(plan, control => Assert.Equal(control.Id, LabProject.ValidateSegmentId(control.Id)));
    }

    [Fact]
    public void Plan_CoversThePlansDeviceControls()
    {
        var ids = LabButtonPlan.For(null).Select(control => control.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var id in new[]
                 {
                     "oem-left", "oem-right", "back-left1", "back-left2", "back-right1", "back-right2",
                     "touchpad-left-click", "touchpad-left-surface", "touchpad-right-click", "touchpad-right-surface",
                     "stick-touch-left", "stick-touch-right", "volume-up", "volume-down", "power",
                     "back-left1-with-a", "back-right1-with-a", "lt", "rt", "left-stick", "right-stick"
                 })
        {
            Assert.Contains(id, ids);
        }
    }

    [Fact]
    public void Plan_NamesKnownButtonsFromTheRecordWithoutDuplicatingThem()
    {
        var plan = LabButtonPlan.For(Record("wsgm.xbox-rog-ally-x"));

        var oem = Assert.Single(plan, control => control.Id == "oem-left");
        Assert.StartsWith("Command Center", oem.Name, StringComparison.Ordinal);
        Assert.Equal("keys F21", oem.Known);
        Assert.Single(plan, control => control.Id == "back-left1");
        Assert.Single(plan, control => control.Id == "volume-up");
    }

    [Fact]
    public void Plan_HoldsAndChordsWaitForTheTester()
    {
        var plan = LabButtonPlan.For(null);

        Assert.All(plan.Where(control => control.Id.EndsWith("-hold", StringComparison.Ordinal)
                                         || control.Id.EndsWith("-with-a", StringComparison.Ordinal)),
            control => Assert.Equal(0, control.QuietMs));
        Assert.Equal(900, plan.Single(control => control.Id == "left-stick").QuietMs);
        Assert.True(plan.Single(control => control.Id == "left-stick").Detailed);
    }

    [Theory]
    [InlineData("OemLeft", "oem-left")]
    [InlineData("BackLeft1", "back-left1")]
    [InlineData("VolumeUp", "volume-up")]
    [InlineData("M1", "m1")]
    [InlineData("Armoury Crate", "armoury-crate")]
    public void Slug_MatchesTheWizardNames(string name, string expected)
    {
        Assert.Equal(expected, LabButtonPlan.Slug(name));
    }

    [Fact]
    public void Keys_CountPressesByKeyUpSoAutoRepeatDoesNotInflateThem()
    {
        LabInputStepRecord step = new()
        {
            Step = "buttons/oem-left",
            StartedMs = 0,
            EndedMs = 2000,
            Events =
            [
                new LabInputEvent(100, "raw-input", "kbd0", "key F21 (VK 84, scan 6C) down"),
                new LabInputEvent(130, "raw-input", "kbd0", "key F21 (VK 84, scan 6C) down"),
                new LabInputEvent(160, "raw-input", "kbd0", "key F21 (VK 84, scan 6C) down"),
                new LabInputEvent(400, "raw-input", "kbd0", "key F21 (VK 84, scan 6C) up"),
                new LabInputEvent(101, "hook", null, "key LWin (VK 5B, scan 5B, flags 01) down swallowed")
            ]
        };

        var keys = LabInputAnalysis.Keys(step);

        var f21 = Assert.Single(keys, key => key.Key == "F21");
        Assert.Equal(1, f21.Presses);
        Assert.Equal(3, f21.Downs);
        Assert.Equal([300.0], f21.HoldsMs);
        Assert.True(Assert.Single(keys, key => key.Key == "LWin").Swallowed);
    }

    [Fact]
    public void Analog_MeasuresStickTravelAndRoundness()
    {
        List<LabInputEvent> events = [];
        for (var i = 0; i < 32; i++)
        {
            var angle = i * Math.PI * 2 / 32;
            var x = (int)(Math.Cos(angle) * 32000);
            var y = (int)(Math.Sin(angle) * 32000);
            events.Add(new LabInputEvent(i * 10, "xinput", "xinput0",
                $"buttons 0000 [] LT 0 RT {i * 8} L {x},{y} R 0,0"));
        }

        var range = Assert.Single(LabInputAnalysis.Analog(new LabInputStepRecord
        {
            Step = "buttons/left-stick", StartedMs = 0, EndedMs = 400, Events = events
        }));

        Assert.Equal(1.0, range.Left.Circularity);
        Assert.True(range.Left.MaxRadius > 0.95);
        Assert.Equal(248, range.RightTriggerMax);
        Assert.Equal(0.0, range.Right.Circularity);
    }

    [Fact]
    public void Candidates_IgnoreBaselineNoiseAndReportChangedBytes()
    {
        LabInputDevice device = new("hid0", "hid", "0B05", "1B4C", 0xFF31, 0x0080, null, false);
        LabInputStepRecord step = new()
        {
            Step = "buttons/oem-left",
            StartedMs = 0,
            EndedMs = 1000,
            Events =
            [
                new LabInputEvent(10, "raw-input", "hid0", "report 5A, 4 bytes", "5A000000", [3], true),
                new LabInputEvent(20, "raw-input", "hid0", "report 5A, 4 bytes", "5AA60000", [1])
            ]
        };

        var candidate = Assert.Single(LabInputAnalysis.Candidates(step, [device],
            DeviceKnowledgeBase.Default.Records.Single(record => record.Id == "wsgm.rog-ally-x")));

        Assert.Equal("vendor", candidate.KnownRole);
        Assert.Contains(candidate.Evidence, item => item.Contains("byte 1: A6", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutPointer_HidesTouchesButKeepsControllers()
    {
        LabInputCandidate touch = new("raw-input", "hid1", "hid 04F3:2C43 000D:0004", ["x"], 1, 1, null);
        LabInputCandidate mouse = new("hook", "injected", null, ["mouse message 0201 data 0"], 1, 1, null);
        LabInputCandidate pad = new("xinput", "xinput0", "xinput 045E:028E", ["A"], 1, 1, null);

        Assert.Equal([pad], LabInputAnalysis.WithoutPointer([touch, mouse, pad]));
    }

    [Fact]
    public void ControllerInit_ClawModeSwitchIsReversibleAndAllyTablesAreNot()
    {
        var claw = LabControllerInit.For(Record("wsgm.claw-8-a2vm"));
        var ally = LabControllerInit.For(Record("wsgm.rog-ally-x"));

        Assert.NotNull(claw);
        Assert.Equal("controller-mode", claw.Feature);
        Assert.True(claw.Reversible);
        Assert.Equal(2, LabControllerInit.TestMode(claw));
        Assert.NotNull(ally);
        Assert.Equal("button-init", ally.Feature);
        Assert.False(ally.Reversible);
        Assert.Null(LabControllerInit.For(Record("wsgm.xbox-rog-ally-x")));
        Assert.Null(LabControllerInit.For(null));
    }

    [Fact]
    public void ControllerInit_IsNeverTakenFromAnExtractedRecord()
    {
        Assert.All(DeviceKnowledgeBase.Default.Records.Where(record => record.Status is DeviceKnowledgeStatus.Extracted),
            record => Assert.Null(LabControllerInit.For(record)));
    }
}
