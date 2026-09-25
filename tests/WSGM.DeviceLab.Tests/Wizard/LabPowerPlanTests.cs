using WSGM.DeviceLab.Knowledge;
using WSGM.DeviceLab.Wizard;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabPowerPlanTests
{
    [Fact]
    public void For_CuratedAsusRecord_ReadsTheAtkAcpiMechanisms()
    {
        var record = DeviceKnowledgeBase.Default.Records.Single(item => item.Id == "wsgm.rog-ally-x");

        var plan = LabPowerPlan.For(record);

        Assert.True(plan.Curated);
        Assert.NotNull(plan.Asus);
        Assert.True(plan.Asus!.HasTdp);
        Assert.True(plan.Asus.CanSnapshot);
        Assert.Equal(5, plan.Asus.MinimumWatts);
        Assert.Equal(25, plan.Asus.MaximumWatts);
        Assert.NotNull(plan.Asus.Charge);
        Assert.Null(plan.Msi);
    }

    [Fact]
    public void For_CuratedMsiRecord_ReadsTheWmiMechanisms()
    {
        var record = DeviceKnowledgeBase.Default.Records.Single(item => item.Id == "wsgm.claw-8-a2vm");

        var plan = LabPowerPlan.For(record);

        Assert.True(plan.Curated);
        Assert.NotNull(plan.Msi);
        Assert.True(plan.Msi!.HasTdp);
        Assert.Equal(8, plan.Msi.MinimumWatts);
        Assert.Equal(37, plan.Msi.MaximumWatts);
        Assert.NotNull(plan.Msi.Charge);
        Assert.NotNull(plan.Msi.FanGetter);
        Assert.Null(plan.Asus);
    }

    [Fact]
    public void For_NullRecord_HasNoTests()
    {
        var plan = LabPowerPlan.For(null);

        Assert.False(plan.Curated);
        Assert.False(plan.HasDeviceTests);
        Assert.Empty(plan.EmbeddedController);
    }

    [Fact]
    public void For_ExtractedEcRecord_RecordsEmbeddedControllerButNoTests()
    {
        var record = DeviceKnowledgeBase.Default.Records.FirstOrDefault(item =>
            item.Status is DeviceKnowledgeStatus.Extracted
            && item.Mechanisms.Any(mechanism => mechanism.Transport == "superio-ec"));
        Assert.NotNull(record);

        var plan = LabPowerPlan.For(record);

        Assert.False(plan.Curated);
        Assert.False(plan.HasDeviceTests);
        Assert.NotEmpty(plan.EmbeddedController);
    }

    [Theory]
    [InlineData(5, 25, new[] { 13, 17, 25 })]
    [InlineData(8, 37, new[] { 13, 17, 25 })]
    [InlineData(10, 12, new[] { 10 })] // No candidate in range: falls back to the quarter-range low.
    public void TestWattsList_StaysInsideTheRange(int min, int max, int[] expected)
    {
        Assert.Equal(expected, LabPowerPlan.TestWattsList(min, max));
    }

    [Fact]
    public void TestWatts_IsAQuarterUpTheRange()
    {
        Assert.Equal(10, LabPowerPlan.TestWatts(5, 25));
    }

    [Fact]
    public void TestChargeLimit_Is80OrElse90()
    {
        Assert.Equal(80, LabPowerPlan.TestChargeLimit(100));
        Assert.Equal(90, LabPowerPlan.TestChargeLimit(80));
    }

    [Fact]
    public void FanTestCurve_RaisesEveryDutyByFifteenPointsAndNeverReduces()
    {
        var original = Curve([30, 40, 50, 60, 70, 80, 90, 100], [10, 20, 30, 40, 50, 60, 70, 90]);

        var test = LabPowerPlan.FanTestCurve(original);

        Assert.NotNull(test);
        Assert.Equal(original.Take(8), test!.Take(8)); // Temperatures unchanged.
        for (var i = 8; i < 16; i++)
        {
            Assert.True(test[i] >= original[i]);
            Assert.True(test[i] <= 99);
        }

        Assert.Equal(25, test[8]); // 10 + 15.
        Assert.Equal(99, test[15]); // 90 + 15 capped at 99.
    }

    [Fact]
    public void FanTestCurve_RejectsAMalformedCurve()
    {
        Assert.Null(LabPowerPlan.FanTestCurve([1, 2, 3]));
        Assert.Null(LabPowerPlan.FanTestCurve(new byte[16])); // All-zero duties are not writable.
    }

    [Fact]
    public void Number_ParsesHexAndDecimal()
    {
        var parameters = new Dictionary<string, string> { ["hex"] = "0x1A", ["dec"] = "42", ["bad"] = "nope" };

        Assert.Equal(0x1Au, LabPowerPlan.Number(parameters, "hex"));
        Assert.Equal(42u, LabPowerPlan.Number(parameters, "dec"));
        Assert.Null(LabPowerPlan.Number(parameters, "bad"));
        Assert.Null(LabPowerPlan.Number(parameters, "missing"));
    }

    [Fact]
    public void ValueList_ParsesValuesAndNames()
    {
        var values = LabPowerPlan.ValueList("0 balanced, 1 turbo, 2 silent");

        Assert.Equal(3, values.Count);
        Assert.Equal((0, "balanced"), values[0]);
        Assert.Equal((1, "turbo"), values[1]);
        Assert.Equal((2, "silent"), values[2]);
    }

    private static byte[] Curve(IEnumerable<int> temperatures, IEnumerable<int> duties)
    {
        return [.. temperatures.Concat(duties).Select(value => (byte)value)];
    }
}
