using WSGM.Controls;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests.Core;

public sealed class FanCurvePresetTests
{
    private static readonly CurveBounds Bounds = new(0, 100, 0, 100);

    private static IReadOnlyList<CurvePoint> ClawTable()
    {
        return
        [
            new CurvePoint(0, 0),
            new CurvePoint(50, 40),
            new CurvePoint(60, 50),
            new CurvePoint(70, 60),
            new CurvePoint(80, 70),
            new CurvePoint(90, 80)
        ];
    }

    /// The samples are HandheldCompanion's own arrays, so a preset read at one of its grid points
    /// must return that entry unchanged rather than something interpolation happened to produce.
    /// Written as one fact rather than a theory because the preset is an internal type, and an
    /// internal parameter on a public test method does not compile.
    [Fact]
    public void APresetReadAtAGridPointIsTheStoredSample()
    {
        (FanCurvePreset Preset, int Celsius, int Expected)[] cases =
        [
            (FanCurvePreset.Quiet, 0, 20),
            (FanCurvePreset.Quiet, 50, 25),
            (FanCurvePreset.Quiet, 80, 70),
            (FanCurvePreset.Quiet, 100, 100),
            (FanCurvePreset.Default, 30, 30),
            (FanCurvePreset.Default, 60, 70),
            (FanCurvePreset.Default, 90, 100),
            (FanCurvePreset.Aggressive, 0, 40),
            (FanCurvePreset.Aggressive, 40, 40),
            (FanCurvePreset.Aggressive, 70, 80)
        ];

        foreach (var (preset, celsius, expected) in cases)
        {
            Assert.Equal(expected, FanCurvePresets.DutyAt(preset, celsius));
        }
    }

    [Fact]
    public void BetweenGridPointsThePresetInterpolates()
    {
        // Default is 50 at 50 °C and 70 at 60 °C, so the midpoint is 60.
        Assert.Equal(60, FanCurvePresets.DutyAt(FanCurvePreset.Default, 55));
    }

    [Fact]
    public void TemperaturesOutsideTheSampledRangeClampRatherThanExtrapolate()
    {
        Assert.Equal(20, FanCurvePresets.DutyAt(FanCurvePreset.Quiet, -40));
        Assert.Equal(100, FanCurvePresets.DutyAt(FanCurvePreset.Quiet, 500));
    }

    /// The temperatures in a fan table are the firmware's breakpoints. A preset says how hard to
    /// blow at a temperature, never which temperatures the table should hold.
    [Fact]
    public void SamplingOntoADeviceCurveKeepsItsOwnTemperatures()
    {
        var sampled =
            FanCurvePresets.SampleOnto(FanCurvePreset.Quiet, ClawTable());

        Assert.Equal(
            ClawTable().Select(point => point.Input),
            sampled.Select(point => point.Input));
    }

    [Fact]
    public void SamplingOntoADeviceCurveTakesThePresetsDuties()
    {
        var sampled =
            FanCurvePresets.SampleOnto(FanCurvePreset.Aggressive, ClawTable());

        Assert.Equal(
            [40, 50, 70, 80, 90, 100],
            sampled.Select(point => point.Output).ToArray());
    }

    /// The three presets have to stay distinguishable once reduced to six points, or the buttons
    /// are three ways to get the same fans.
    [Fact]
    public void TheThreePresetsRemainDistinctOnTheDevicesOwnBreakpoints()
    {
        int[][] shapes =
        [
            [.. FanCurvePresets.SampleOnto(FanCurvePreset.Quiet, ClawTable()).Select(p => p.Output)],
            [.. FanCurvePresets.SampleOnto(FanCurvePreset.Default, ClawTable()).Select(p => p.Output)],
            [.. FanCurvePresets.SampleOnto(FanCurvePreset.Aggressive, ClawTable()).Select(p => p.Output)]
        ];

        Assert.NotEqual(shapes[0], shapes[1]);
        Assert.NotEqual(shapes[1], shapes[2]);
        Assert.NotEqual(shapes[0], shapes[2]);
    }

    [Fact]
    public void EveryPresetProducesDutiesThatNeverDecrease()
    {
        foreach (var preset in Enum.GetValues<FanCurvePreset>())
        {
            var sampled = FanCurvePresets.SampleOnto(preset, ClawTable());
            for (var index = 1; index < sampled.Count; index++)
            {
                Assert.True(
                    sampled[index].Output >= sampled[index - 1].Output,
                    $"{preset} dips at index {index}.");
            }
        }
    }

    [Fact]
    public void ADeviceThatPublishedNoCurveGetsNothingRatherThanAnInventedOne()
    {
        Assert.Empty(FanCurvePresets.SampleOnto(FanCurvePreset.Default, []));
    }

    /// Only the Atlas has a measured table of its own; every other definition keeps HandheldCompanion's.
    [Fact]
    public void TheMeasuredAtlasTableIsChosenByItsExactDefinitionOnly()
    {
        Assert.Same(FanCurvePresetTable.AcerAtlas8, FanCurvePresets.For(FanCurvePresets.AcerAtlas8DefinitionId));
        Assert.Same(FanCurvePresetTable.HandheldCompanion, FanCurvePresets.For("msi-claw-8-ai-plus-a2vm"));
        Assert.Same(FanCurvePresetTable.HandheldCompanion, FanCurvePresets.For(null));
    }

    /// The Atlas fans stall below 10% and the SoC idles at 52..66 °C, so every shape rests at a quiet,
    /// spinning floor through 50 °C and only reaches full speed at the processor's 100 °C limit.
    [Fact]
    public void TheAtlasShapesIdleQuietlyRiseWithoutDipsAndStayDistinct()
    {
        var table = FanCurvePresetTable.AcerAtlas8;
        var inputs = Enumerable.Range(0, 11).Select(index => new CurvePoint(index * 10, 0)).ToArray();
        var shapes = Enum.GetValues<FanCurvePreset>()
            .Select(preset => FanCurvePresets.SampleOnto(table, preset, inputs).Select(point => point.Output).ToArray())
            .ToArray();
        foreach (var shape in shapes)
        {
            Assert.InRange(shape[5], 10, 35);
            Assert.Equal(100, shape[10]);
            Assert.True(shape.Zip(shape.Skip(1)).All(pair => pair.Second >= pair.First));
            Assert.True(shape.All(duty => duty >= 10));
        }

        Assert.Equal(3, shapes.Distinct(new ArrayComparer()).Count());
        // Default is 45 at 70 °C and 65 at 80 °C, so the midpoint is 55.
        Assert.Equal(45, FanCurvePresets.DutyAt(table, FanCurvePreset.Default, 70));
        Assert.Equal(55, FanCurvePresets.DutyAt(table, FanCurvePreset.Default, 75));
    }

    private sealed class ArrayComparer : IEqualityComparer<int[]>
    {
        public bool Equals(int[]? x, int[]? y)
        {
            return x is not null && y is not null && x.SequenceEqual(y);
        }

        public int GetHashCode(int[] obj)
        {
            return obj.Length;
        }
    }

    /// The firmware refuses a fan table whose duties dip, so a drag that would make one is held
    /// against its neighbours rather than failing on apply with nothing to show for it.
    [Fact]
    public void ARisingCurveRefusesADragBelowTheLeftNeighbour()
    {
        var moved = CurveEditing.Move(
            ClawTable(),
            3,
            70,
            10,
            Bounds,
            true);

        Assert.Equal(50, moved[3].Output);
    }

    [Fact]
    public void ARisingCurveRefusesADragAboveTheRightNeighbour()
    {
        var moved = CurveEditing.Move(
            ClawTable(),
            3,
            70,
            95,
            Bounds,
            true);

        Assert.Equal(70, moved[3].Output);
    }

    [Fact]
    public void ARisingCurveStillMovesAPointBetweenItsNeighbours()
    {
        var moved = CurveEditing.Move(
            ClawTable(),
            3,
            70,
            55,
            Bounds,
            true);

        Assert.Equal(55, moved[3].Output);
    }

    /// Without the rule the same drag is accepted, which is what the authored lighting curves want.
    [Fact]
    public void WithoutTheRuleADipIsStillAllowed()
    {
        var moved = CurveEditing.Move(
            ClawTable(),
            3,
            70,
            10,
            Bounds);

        Assert.Equal(10, moved[3].Output);
    }

    /// Dragging a point that currently dips pulls it up to its left neighbour, which is how the
    /// dip gets resolved rather than preserved.
    [Fact]
    public void ADippingPointIsLiftedToItsLeftNeighbour()
    {
        IReadOnlyList<CurvePoint> dipped =
        [
            new(0, 0),
            new(50, 90),
            new(60, 10),
            new(70, 95)
        ];

        var moved = CurveEditing.Move(
            dipped,
            2,
            60,
            50,
            Bounds,
            true);

        Assert.Equal(90, moved[2].Output);
    }

    /// When the NEIGHBOURS themselves cross there is no legal range between them, and clamping into
    /// an inverted one would snap the point somewhere the user did not ask for. The request stands
    /// so the offending pair can be dragged back into shape one point at a time.
    [Fact]
    public void CrossedNeighboursLeaveTheRequestedValueAlone()
    {
        IReadOnlyList<CurvePoint> crossed =
        [
            new(0, 0),
            new(50, 90),
            new(60, 60),
            new(70, 20)
        ];

        var moved = CurveEditing.Move(
            crossed,
            2,
            60,
            50,
            Bounds,
            true);

        Assert.Equal(50, moved[2].Output);
    }
}
