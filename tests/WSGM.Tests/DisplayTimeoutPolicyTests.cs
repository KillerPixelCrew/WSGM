using WSGM.Core;

namespace WSGM.Tests;

/// <summary>
/// The order WSGM keeps between Steam's screensaver timeout and the display-off timeout: the display
/// may never turn off before the screensaver is allowed to start.
/// </summary>
public sealed class DisplayTimeoutPolicyTests
{
    [Fact]
    public void NothingBoundsTheDisplayBeforeSteamReports()
    {
        Assert.Null(DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.DisplayAc, null));
        Assert.Null(DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.DisplayDc, null));
    }

    [Fact]
    public void WithoutABatterySteamsOneTimeoutBoundsBothSources()
    {
        // Steam shows only the plugged-in timeout on a machine it believes has no battery, and a
        // battery value it holds but never shows is not what the user chose.
        SteamScreensaverReport steam = new(300, 1800, Battery: false);

        Assert.Equal(300, DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.DisplayAc, steam));
        Assert.Equal(300, DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.DisplayDc, steam));
    }

    [Fact]
    public void WithABatteryEachSourceHasItsOwnBound()
    {
        SteamScreensaverReport steam = new(300, 900, Battery: true);

        Assert.Equal(300, DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.DisplayAc, steam));
        Assert.Equal(900, DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.DisplayDc, steam));
    }

    [Fact]
    public void AnUnsetBatteryTimeoutFallsBackToThePluggedInOne()
    {
        Assert.Equal(
            300,
            DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.DisplayDc, new SteamScreensaverReport(300, null, Battery: true)));
    }

    [Fact]
    public void ADisabledScreensaverBoundsNothingAndSleepIsNeverBound()
    {
        SteamScreensaverReport steam = new(0, 0, Battery: true);

        Assert.Null(DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.DisplayAc, steam));
        Assert.Null(DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.DisplayDc, steam));
        Assert.Null(DisplayTimeoutPolicy.Minimum(PowerTimeoutKind.SleepAc, new SteamScreensaverReport(300, 300, false)));
    }

    [Theory]
    [InlineData(600, null, true)]
    [InlineData(600, 300, true)]
    [InlineData(300, 300, true)]
    [InlineData(180, 300, false)]
    // Never keeps the display on, which is later than any screensaver.
    [InlineData(0, 3600, true)]
    public void TheOrderHoldsAtOrAboveTheBound(int seconds, int? minimum, bool allowed)
    {
        Assert.Equal(allowed, DisplayTimeoutPolicy.Allows(seconds, minimum));
    }

    [Theory]
    [InlineData(45, 60)]
    [InlineData(240, 300)]
    [InlineData(300, 300)]
    [InlineData(1000, 1800)]
    // Beyond the longest preset the bound itself is used rather than Never.
    [InlineData(7200, 7200)]
    public void ABreachIsRaisedToTheShortestPresetAtOrAboveTheBound(int minimum, int raised)
    {
        Assert.Equal(raised, DisplayTimeoutPolicy.Raised(minimum));
    }

    [Theory]
    [InlineData(60, 300, 300)]
    [InlineData(0, 300, 300)]
    [InlineData(3600, 300, 0)]
    [InlineData(60, null, 180)]
    public void CyclingSkipsPresetsTheScreensaverForbids(int current, int? minimum, int next)
    {
        Assert.Equal(next, DisplayTimeoutPolicy.NextAllowed(current, minimum));
    }

    [Fact]
    public void ChoicesAreTheAllowedPresetsShortestFirstWithNeverLast()
    {
        Assert.Equal([300, 600, 900, 1800, 3600, 0], DisplayTimeoutPolicy.Choices(600, 300));
    }

    [Fact]
    public void AnUnusualCurrentValueIsAmongTheChoicesSoTheRowCanShowIt()
    {
        Assert.Equal([60, 180, 300, 450, 600, 900, 1800, 3600, 0], DisplayTimeoutPolicy.Choices(450, null));
        Assert.Equal([60, 180, 300, 600, 900, 1800, 3600, 0], DisplayTimeoutPolicy.Choices(0, null));
    }
}
