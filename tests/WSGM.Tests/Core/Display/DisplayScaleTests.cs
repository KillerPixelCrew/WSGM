using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class DisplayScaleTests
{
    [Theory]
    [InlineData(150, 100u, 100u, 150u)] // saved desktop scaling wins
    [InlineData(null, 100u, 150u, 150u)] // desktop already ran 100% → panel's recommended
    [InlineData(null, 175u, 150u, 175u)] // live desktop scaling beats recommended
    [InlineData(null, 100u, 100u, 100u)] // nothing known → no upscale
    [InlineData(99, 100u, 150u, 150u)] // garbage snapshot value is ignored
    [InlineData(600, 100u, 150u, 150u)]
    public void UiScaleUsesTheSavedDesktopScalingElseTheRecommendedPanelScale(
        int? saved, uint current, uint recommended, uint expected)
    {
        Assert.Equal(expected, DisplayScale.PickUiScalePercent(saved, current, recommended));
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
