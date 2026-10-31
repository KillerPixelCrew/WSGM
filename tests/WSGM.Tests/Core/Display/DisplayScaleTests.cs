using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class DisplayScaleTests
{
    [Theory]
    [InlineData(100u, 150u, 150u)] // desktop already ran 100% → panel's recommended
    [InlineData(175u, 150u, 175u)] // live desktop scaling beats recommended
    [InlineData(100u, 100u, 100u)] // nothing known → no upscale
    [InlineData(50u, 75u, 100u)] // never below 100%
    public void WithoutASavedScaleTheUiUsesTheLargerOfTheCurrentAndRecommendedPanelScale(
        uint current, uint recommended, uint expected)
    {
        Assert.Equal(expected, DisplayScale.PickUiScalePercent(current, recommended));
    }

    [Fact]
    public void ANewDockDisplayIsNotLoweredWhileAnotherDisplaysRecoverySnapshotSurvives()
    {
        Assert.False(DisplayScale.ShouldLowerDisplay(
            false,
            [new DisplayScaleEntry { DeviceName = @"\\.\DISPLAY1", Percent = 150 }],
            @"\\.\DISPLAY2"));
    }

    [Fact]
    public void ADisplayAlreadyOwnedByTheRecoverySnapshotCanBeLoweredAgain()
    {
        Assert.True(DisplayScale.ShouldLowerDisplay(
            false,
            [new DisplayScaleEntry { DeviceName = @"\\.\DISPLAY1", Percent = 150 }],
            @"\\.\display1"));
    }

    [Fact]
    public void AFreshCaptureCanLowerEveryIdentifiedDisplay()
    {
        Assert.True(DisplayScale.ShouldLowerDisplay(
            true,
            [],
            @"\\.\DISPLAY2"));
    }
}
