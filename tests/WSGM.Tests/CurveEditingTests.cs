using WSGM.Controls;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Tests;

public sealed class CurveEditingTests
{
    private static readonly CurveBounds Fan = new(0, 100, 0, 100);

    private static CurvePoint[] Curve(params (int Input, int Output)[] points) =>
        [.. points.Select(point => new CurvePoint(point.Input, point.Output))];

    [Fact]
    public void NormalizeOrdersPointsAndClampsThemIntoTheDevicesBounds()
    {
        IReadOnlyList<CurvePoint> curve = CurveEditing.Normalize(
            Curve((80, 150), (20, -10), (50, 40)),
            Fan);

        Assert.Equal(Curve((20, 0), (50, 40), (80, 100)), curve);
    }

    [Fact]
    public void NormalizeTurnsAnEmptyCurveIntoOneTheUserCanDrag()
    {
        // The router refuses an empty curve, and an empty input is a missing value rather than a
        // request for silence.
        IReadOnlyList<CurvePoint> curve = CurveEditing.Normalize([], Fan);

        Assert.Equal(2, curve.Count);
        Assert.True(CurveEditing.IsValid(curve, Fan));
    }

    [Fact]
    public void NormalizeCollapsesDuplicateInputsBecauseTheContractForbidsThem()
    {
        IReadOnlyList<CurvePoint> curve = CurveEditing.Normalize(
            Curve((0, 10), (50, 20), (50, 60), (100, 80)),
            Fan);

        Assert.Equal(Curve((0, 10), (50, 60), (100, 80)), curve);
        Assert.True(CurveEditing.IsValid(curve, Fan));
    }

    [Fact]
    public void APointStopsAgainstItsNeighbourRatherThanPassingIt()
    {
        // Reordering mid-drag makes the point under the finger a different point, which reads as
        // the curve snapping away.
        IReadOnlyList<CurvePoint> curve = CurveEditing.Move(
            Curve((0, 0), (40, 40), (60, 60), (100, 100)),
            index: 1,
            input: 95,
            output: 50,
            Fan);

        Assert.Equal(59, curve[1].Input);
        Assert.True(CurveEditing.IsValid(curve, Fan));
    }

    [Fact]
    public void EndpointsKeepTheirInputsSoTheCurveAlwaysSpansTheDevicesRange()
    {
        // A fan curve that no longer reaches its highest temperature has an undefined answer there.
        IReadOnlyList<CurvePoint> curve = CurveEditing.Move(
            Curve((0, 0), (50, 50), (100, 100)),
            index: 2,
            input: 70,
            output: 80,
            Fan);

        Assert.Equal(100, curve[2].Input);
        Assert.Equal(80, curve[2].Output);
    }

    [Fact]
    public void MovingAPointStillClampsItsOutput()
    {
        IReadOnlyList<CurvePoint> curve = CurveEditing.Move(
            Curve((0, 0), (50, 50), (100, 100)),
            index: 1,
            input: 50,
            output: 400,
            Fan);

        Assert.Equal(100, curve[1].Output);
    }

    [Fact]
    public void AddingAtAnOccupiedInputMovesThatPointRatherThanDuplicatingIt()
    {
        IReadOnlyList<CurvePoint> curve = CurveEditing.Add(
            Curve((0, 0), (50, 50), (100, 100)),
            input: 50,
            output: 70,
            Fan);

        Assert.Equal(3, curve.Count);
        Assert.Equal(70, curve[1].Output);
        Assert.True(CurveEditing.IsValid(curve, Fan));
    }

    [Fact]
    public void AddedPointsLandInOrder()
    {
        IReadOnlyList<CurvePoint> curve = CurveEditing.Add(
            Curve((0, 0), (100, 100)),
            input: 30,
            output: 25,
            Fan);

        Assert.Equal(Curve((0, 0), (30, 25), (100, 100)), curve);
    }

    [Fact]
    public void AFullCurveRefusesANewPointRatherThanDroppingAnExistingOne()
    {
        CurvePoint[] full = [.. Enumerable.Range(0, CurveEditing.MaximumPoints)
            .Select(index => new CurvePoint(index, index))];

        IReadOnlyList<CurvePoint> curve = CurveEditing.Add(full, input: 90, output: 90, Fan);

        Assert.Equal(CurveEditing.MaximumPoints, curve.Count);
        Assert.True(CurveEditing.IsValid(curve, Fan));
    }

    [Fact]
    public void TheEndpointsCannotBeRemoved()
    {
        CurvePoint[] curve = Curve((0, 0), (50, 50), (100, 100));

        Assert.Same(curve, CurveEditing.Remove(curve, 0));
        Assert.Same(curve, CurveEditing.Remove(curve, 2));
    }

    [Fact]
    public void AnInteriorPointIsRemoved()
    {
        IReadOnlyList<CurvePoint> curve = CurveEditing.Remove(
            Curve((0, 0), (50, 50), (100, 100)),
            1);

        Assert.Equal(Curve((0, 0), (100, 100)), curve);
    }

    [Fact]
    public void ATwoPointCurveIsTheFloor()
    {
        CurvePoint[] curve = Curve((0, 0), (100, 100));

        Assert.Same(curve, CurveEditing.Remove(curve, 1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(25, 25)]
    [InlineData(100, 100)]
    public void EvaluateInterpolatesBetweenPoints(int input, int expected)
    {
        Assert.Equal(expected, CurveEditing.Evaluate(Curve((0, 0), (100, 100)), input));
    }

    [Theory]
    [InlineData(-40, 20)]
    [InlineData(400, 80)]
    public void EvaluateHoldsFlatOutsideTheCurvesOwnRange(int input, int expected)
    {
        Assert.Equal(expected, CurveEditing.Evaluate(Curve((10, 20), (90, 80)), input));
    }

    [Fact]
    public void EvaluateRoundsRatherThanTruncating()
    {
        // A duty cycle reading one below the point the user placed looks like a lost edit.
        // Two thirds of the way up a 0-100 rise is 66.67: rounding gives 67, truncation 66.
        Assert.Equal(67, CurveEditing.Evaluate(Curve((0, 0), (3, 100)), 2));
        Assert.Equal(33, CurveEditing.Evaluate(Curve((0, 0), (3, 100)), 1));
    }

    [Theory]
    [InlineData(0, 0, 100, 100, true)]
    // A falling curve is legal: only the INPUTS must ascend. Nothing says a device's output has to
    // rise with its input, and refusing this would rule out every inverted control.
    [InlineData(0, 100, 100, 0, true)]
    [InlineData(100, 0, 0, 100, false)]
    [InlineData(50, 0, 50, 100, false)]
    public void ValidityMatchesTheContractTheRouterEnforces(
        int firstInput,
        int firstOutput,
        int secondInput,
        int secondOutput,
        bool expected)
    {
        Assert.Equal(
            expected,
            CurveEditing.IsValid(
                Curve((firstInput, firstOutput), (secondInput, secondOutput)),
                Fan));
    }

    [Fact]
    public void AnEmptyCurveIsNotValid()
    {
        Assert.False(CurveEditing.IsValid([], Fan));
    }

    [Fact]
    public void BoundsWithNoRangeAreNotUsable()
    {
        Assert.False(new CurveBounds(0, 0, 0, 100).IsUsable);
        Assert.True(Fan.IsUsable);
    }
}
