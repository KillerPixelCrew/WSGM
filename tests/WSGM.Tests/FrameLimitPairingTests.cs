using WSGM.Core;

namespace WSGM.Tests;

public sealed class FrameLimitPairingTests
{
    // What the reference Claw actually reports: the panel advertises 60 and 120, while the driver
    // accepts four more it synthesizes inside the 30-120 adaptive-sync range.
    private static readonly int[] ClawNative = [60, 120];
    private static readonly int[] ClawAccepted = [30, 48, 60, 75, 100, 120];

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void SelectRefreshHz_FrameLimitOnly_NeverTouchesTheRefreshRate(int cap)
    {
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameLimitOnly, cap, ClawNative, ClawAccepted));
    }

    [Theory]
    [InlineData(24, 48)]
    [InlineData(25, 75)]
    [InlineData(30, 30)]
    [InlineData(40, 120)]
    [InlineData(50, 100)]
    [InlineData(60, 60)]
    public void SelectRefreshHz_FrameDoubling_TakesTheLowestExactMultiple(int cap, int expected)
    {
        Assert.Equal(expected, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, cap, ClawNative, ClawAccepted));
    }

    [Theory]
    [InlineData(30, 60)]
    [InlineData(60, 60)]
    [InlineData(40, 120)]
    public void SelectRefreshHz_NativeModes_UsesOnlyWhatThePanelAdvertises(int cap, int expected)
    {
        Assert.Equal(expected, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.NativeModes, cap, ClawNative, ClawAccepted));
    }

    [Fact]
    public void SelectRefreshHz_NativeModes_LeavesRefreshAloneWhenOnlyASynthesizedModeWouldFit()
    {
        // 48 divides only the synthesized 48 Hz; neither advertised rate is a multiple of it.
        // Note 24 would NOT do as an example here — 120 is an exact 5x of it, so the panel's own
        // modes can hold a 24 FPS cadence perfectly well.
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.NativeModes, 48, ClawNative, ClawAccepted));
        Assert.Equal(48, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 48, ClawNative, ClawAccepted));
    }

    [Fact]
    public void SelectRefreshHz_CapWithNoExactMultiple_LeavesRefreshAlone()
    {
        // 45 divides none of 30/48/60/75/100/120, so forcing a mode would introduce judder rather
        // than remove it.
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 45, ClawNative, ClawAccepted));
    }

    [Fact]
    public void SelectRefreshHz_UncappedOrAbsurdlyLowCap_LeavesRefreshAlone()
    {
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 0, ClawNative, ClawAccepted));
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 5, ClawNative, ClawAccepted));
    }

    [Fact]
    public void SelectRefreshHz_PanelWithTwoModesOnly_StillPairsWhatItCan()
    {
        // The Legion Go case that motivated the strategy split.
        int[] twoModes = [30, 60];

        Assert.Equal(30, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 30, twoModes, twoModes));
        Assert.Equal(60, FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 20, twoModes, twoModes));
        Assert.Null(FrameLimitPairing.SelectRefreshHz(
            FrameLimitStrategy.FrameDoubling, 40, twoModes, twoModes));
    }

    [Fact]
    public void FrameLimitOptions_AlwaysOffersOffFirst()
    {
        foreach (FrameLimitStrategy strategy in System.Enum.GetValues<FrameLimitStrategy>())
        {
            IReadOnlyList<int> options =
                FrameLimitPairing.FrameLimitOptions(strategy, ClawNative, ClawAccepted);
            Assert.Equal(0, options[0]);
        }
    }

    [Fact]
    public void FrameLimitOptions_CoupledStrategy_OffersOnlyCapsWithAnExactCadence()
    {
        IReadOnlyList<int> options = FrameLimitPairing.FrameLimitOptions(
            FrameLimitStrategy.FrameDoubling, ClawNative, ClawAccepted);

        foreach (int cap in options.Where(cap => cap != 0))
        {
            Assert.NotNull(FrameLimitPairing.SelectRefreshHz(
                FrameLimitStrategy.FrameDoubling, cap, ClawNative, ClawAccepted));
        }
    }

    [Fact]
    public void FrameLimitOptions_CoupledStrategy_IncludesCapsDerivedFromUnusualModes()
    {
        IReadOnlyList<int> options = FrameLimitPairing.FrameLimitOptions(
            FrameLimitStrategy.FrameDoubling, ClawNative, ClawAccepted);

        // 25 is only reachable because the driver accepts 75 Hz; a fixed ladder would have missed it.
        Assert.Contains(25, options);
        Assert.Contains(24, options);
    }

    [Fact]
    public void FrameLimitOptions_NeverExceedsTheFastestAvailableMode()
    {
        IReadOnlyList<int> options = FrameLimitPairing.FrameLimitOptions(
            FrameLimitStrategy.FrameLimitOnly, ClawNative, ClawAccepted);

        Assert.All(options, cap => Assert.True(cap <= 120));
    }

    [Fact]
    public void FrameLimitOptions_NoUsableModes_OffersOnlyOff()
    {
        Assert.Equal([0], FrameLimitPairing.FrameLimitOptions(
            FrameLimitStrategy.FrameDoubling, [], []));
    }

    [Fact]
    public void RefreshRateIsUserOwned_OnlyWhenNothingElseIsMovingIt()
    {
        Assert.True(FrameLimitPairing.RefreshRateIsUserOwned(FrameLimitStrategy.FrameLimitOnly));
        Assert.False(FrameLimitPairing.RefreshRateIsUserOwned(FrameLimitStrategy.NativeModes));
        Assert.False(FrameLimitPairing.RefreshRateIsUserOwned(FrameLimitStrategy.FrameDoubling));
    }
}
