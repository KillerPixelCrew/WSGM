using WSGM.Plugin.IntelGpu.Controls;
using Xunit;

namespace WSGM.Plugin.IntelGpu.Tests;

public sealed class ValueMappingTests
{
    [Fact]
    public void AFloatRangeIsScaledToWholeSteps()
    {
        var range = ValueMapping.FromFloat(0f, 1f, 0.1f, false)!.Value;

        Assert.Equal((0, 10, 1, 10), (range.Minimum, range.Maximum, range.Step, range.Scale));
        Assert.Equal(7, range.ToInteger(0.7));
        Assert.Equal(0.3, range.ToNative(3), 6);
    }

    [Fact]
    public void AnIntegralFloatRangeKeepsItsUnits()
    {
        var range = ValueMapping.FromFloat(0f, 100f, 1f, false)!.Value;

        Assert.Equal((0, 100, 1, 1), (range.Minimum, range.Maximum, range.Step, range.Scale));
    }

    [Fact]
    public void ARangeThatContainsZeroKeepsItsOwnZero()
    {
        var range = ValueMapping.FromInt(-5, 5, 1, true)!.Value;

        Assert.False(range.OffAtZero);
        Assert.Equal(-5, range.Minimum);
    }

    [Fact]
    public void OffAtZeroAcceptsZeroAndTheDriverRangeOnly()
    {
        var range = ValueMapping.FromUInt(30, 240, 1, true)!.Value;

        Assert.True(range.Accepts(0));
        Assert.False(range.Accepts(29));
        Assert.True(range.Accepts(30));
        Assert.True(range.Accepts(240));
        Assert.False(range.Accepts(241));
    }

    [Fact]
    public void AnOffGridDriverValueIsPublishedOnTheNearestStep()
    {
        // WSGM's router refuses an integer off the descriptor's step grid.
        var range = IntegerRange.Linear(0, 100, 5);

        Assert.Equal(10, range.ToInteger(12));
        Assert.Equal(15, range.ToInteger(13));
        Assert.Equal(100, range.ToInteger(250));
        Assert.Equal(0, range.ToInteger(-3));
    }

    [Fact]
    public void AMaximumOffTheGridSnapsDownIntoTheRange()
    {
        var range = IntegerRange.Linear(0, 10, 3);

        Assert.Equal(9, range.ToInteger(10));
    }

    [Fact]
    public void AMisalignedStepFallsBackToOne()
    {
        var range = ValueMapping.FromInt(32, 132, 5, true)!.Value;

        Assert.Equal(1, range.Step);
    }

    [Fact]
    public void ADegenerateRangeIsNotAControl()
    {
        Assert.Null(ValueMapping.FromInt(10, 10, 1, false));
        Assert.Null(ValueMapping.FromFloat(float.NaN, 1, 0.1f, false));
    }

    [Theory]
    [InlineData((ushort)0xe509, "boe")]
    [InlineData((ushort)0x09e5, "boe")]
    [InlineData((ushort)0x2d4c, "sam")]
    public void TheEdidManufacturerDecodesInEitherByteOrder(ushort raw, string expected)
    {
        Assert.Equal(expected, ValueMapping.DecodeManufacturer(raw));
    }

    [Fact]
    public void TheFingerprintIsStableAndCaseBlind()
    {
        const string path = @"\\?\DISPLAY#BOE0B78#4&2a8b4c3&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

        Assert.Equal(ValueMapping.Fingerprint(path), ValueMapping.Fingerprint(path.ToLowerInvariant()));
        Assert.Matches("^[0-9a-f]{8}$", ValueMapping.Fingerprint(path));
    }
}
