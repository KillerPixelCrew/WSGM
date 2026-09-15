using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ExplorerReadinessTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    private static ExplorerReadinessAction Decide(
        bool shellWindow, bool taskbar, bool bigPicture,
        double elapsedSeconds, double? settleSeconds)
        => ExplorerReadiness.Decide(
            shellWindow, taskbar, bigPicture,
            TimeSpan.FromSeconds(elapsedSeconds),
            settleSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            Settle, MaxWait);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WaitsWhileExplorerWindowsAreIncomplete(bool shellWindow, bool taskbar)
    {
        Assert.Equal(ExplorerReadinessAction.Wait,
            Decide(shellWindow, taskbar, bigPicture: false, elapsedSeconds: 3, settleSeconds: null));
    }

    [Fact]
    public void BothWindowsPresentBeginTheSettle()
    {
        Assert.Equal(ExplorerReadinessAction.BeginSettle,
            Decide(shellWindow: true, taskbar: true, bigPicture: false, elapsedSeconds: 4, settleSeconds: null));
    }

    [Fact]
    public void WaitsUntilTheSettleElapses()
    {
        Assert.Equal(ExplorerReadinessAction.Wait,
            Decide(shellWindow: true, taskbar: true, bigPicture: false, elapsedSeconds: 6, settleSeconds: 2));
    }

    [Fact]
    public void ProceedsOnceTheSettleElapsed()
    {
        Assert.Equal(ExplorerReadinessAction.Proceed,
            Decide(shellWindow: true, taskbar: true, bigPicture: false, elapsedSeconds: 10, settleSeconds: 5));
    }

    [Fact]
    public void SettleSurvivesExplorerWindowsVanishingAgain()
    {
        // A crashing explorer mid-settle must not reset anything — the takeover
        // shuts explorer down regardless.
        Assert.Equal(ExplorerReadinessAction.Proceed,
            Decide(shellWindow: false, taskbar: false, bigPicture: false, elapsedSeconds: 12, settleSeconds: 6));
    }

    [Fact]
    public void BigPictureUnderTheCoverAcceleratesImmediately()
    {
        // Invariant 7: never stay opaque over a live BP window — outranks the settle.
        Assert.Equal(ExplorerReadinessAction.ProceedAccelerated,
            Decide(shellWindow: true, taskbar: true, bigPicture: true, elapsedSeconds: 6, settleSeconds: 1));
    }

    [Fact]
    public void BigPictureAccelerationOutranksTheTimeout()
    {
        Assert.Equal(ExplorerReadinessAction.ProceedAccelerated,
            Decide(shellWindow: false, taskbar: false, bigPicture: true, elapsedSeconds: 90, settleSeconds: null));
    }

    [Fact]
    public void HardCapProceedsWithoutExplorerEverBecomingReady()
    {
        Assert.Equal(ExplorerReadinessAction.ProceedTimeout,
            Decide(shellWindow: false, taskbar: false, bigPicture: false, elapsedSeconds: 60, settleSeconds: null));
    }

    [Fact]
    public void ZeroSettleProceedsImmediatelyAfterBeginSettle()
    {
        Assert.Equal(ExplorerReadinessAction.Proceed,
            ExplorerReadiness.Decide(true, true, false,
                TimeSpan.FromSeconds(3), TimeSpan.Zero, TimeSpan.Zero, MaxWait));
    }
}
