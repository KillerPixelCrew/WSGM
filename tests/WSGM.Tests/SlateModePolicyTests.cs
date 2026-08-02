using WSGM.Core;

namespace WSGM.Tests;

public sealed class SlateModePolicyTests
{
    [Theory]
    [InlineData(false, -1, false)]
    [InlineData(true, -1, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, true)]
    public void ConvertibleSlateModeIsChangedOnlyWhenItWasCapturedAsExisting(bool captured, int previous, bool expected)
    {
        Assert.Equal(expected, SlateMode.ShouldOverrideConvertibleSlateMode(captured, previous));
    }

    [Fact]
    public void LegacySlateModeSnapshotIsRestoredOnce()
    {
        Assert.True(SlateMode.ShouldRestoreConvertibleSlateMode(modifiedByWsgm: null));
    }

    [Fact]
    public void CurrentAbsentSlateModeIsNeverRestoredOrDeleted()
    {
        Assert.False(SlateMode.ShouldRestoreConvertibleSlateMode(modifiedByWsgm: false));
    }
}
