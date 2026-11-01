using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class ExplorerReadinessTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    private static ExplorerReadinessAction Decide(
        bool shellWindow, bool taskbar, bool bigPicture,
        double elapsedSeconds, double? settleSeconds)
    {
        return ExplorerReadiness.Decide(
            shellWindow, taskbar, bigPicture,
            TimeSpan.FromSeconds(elapsedSeconds),
            settleSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            Settle, MaxWait);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WaitsWhileExplorerWindowsAreIncomplete(bool shellWindow, bool taskbar)
    {
        Assert.Equal(ExplorerReadinessAction.Wait,
            Decide(shellWindow, taskbar, false, 3, null));
    }

    [Fact]
    public void BothWindowsPresentBeginTheSettle()
    {
        Assert.Equal(ExplorerReadinessAction.BeginSettle,
            Decide(true, true, false, 4, null));
    }

    [Fact]
    public void WaitsUntilTheSettleElapses()
    {
        Assert.Equal(ExplorerReadinessAction.Wait,
            Decide(true, true, false, 6, 2));
    }

    [Fact]
    public void ProceedsOnceTheSettleElapsed()
    {
        Assert.Equal(ExplorerReadinessAction.Proceed,
            Decide(true, true, false, 10, 5));
    }

    [Fact]
    public void SettleSurvivesExplorerWindowsVanishingAgain()
    {
        // A crashing explorer mid-settle must not reset anything — the takeover
        // shuts explorer down regardless.
        Assert.Equal(ExplorerReadinessAction.Proceed,
            Decide(false, false, false, 12, 6));
    }

    [Fact]
    public void BigPictureUnderTheCoverAcceleratesImmediately()
    {
        // Invariant 7: never stay opaque over a live BP window — outranks the settle.
        Assert.Equal(ExplorerReadinessAction.ProceedAccelerated,
            Decide(true, true, true, 6, 1));
    }

    [Fact]
    public void BigPictureAccelerationOutranksTheTimeout()
    {
        Assert.Equal(ExplorerReadinessAction.ProceedAccelerated,
            Decide(false, false, true, 90, null));
    }

    [Fact]
    public void HardCapProceedsWithoutExplorerEverBecomingReady()
    {
        Assert.Equal(ExplorerReadinessAction.ProceedTimeout,
            Decide(false, false, false, 60, null));
    }

    [Fact]
    public void ZeroSettleProceedsImmediatelyAfterBeginSettle()
    {
        Assert.Equal(ExplorerReadinessAction.Proceed,
            ExplorerReadiness.Decide(true, true, false,
                TimeSpan.FromSeconds(3), TimeSpan.Zero, TimeSpan.Zero, MaxWait));
    }
}
