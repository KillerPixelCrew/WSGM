using System.Diagnostics;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SteamUiTransportGateTests
{
    // One session's readiness; every test starts closed.
    private readonly SteamUiReadiness _readiness = new();

    [Theory]
    [InlineData((int)SteamUiHold.Activation)]
    [InlineData((int)SteamUiHold.Suspended)]
    [InlineData((int)SteamUiHold.Resuming)]
    [InlineData((int)SteamUiHold.BigPictureClose)]
    public void EveryHoldButTheRequestClosesTheTransportWhateverTheWindowSays(int holdBits)
    {
        var hold = (SteamUiHold)holdBits;
        foreach (var inGameMode in (bool[])[false, true])
        {
            foreach (var bigPictureReady in (bool[])[false, true])
            {
                Assert.False(SteamUiReadiness.TransportShouldBeOpen(true, inGameMode, hold, bigPictureReady));
                Assert.False(SteamUiReadiness.TransportShouldBeOpen(true, inGameMode,
                    hold | SteamUiHold.BigPictureRequest, bigPictureReady));
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WithoutAHoldGameModeWaitsForBigPictureAndDesktopModeDoesNot(bool inGameMode, bool bigPictureReady)
    {
        Assert.Equal(!inGameMode || bigPictureReady,
            SteamUiReadiness.TransportShouldBeOpen(true, inGameMode, SteamUiHold.None, bigPictureReady));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABigPictureRequestHoldsUntilTheWindowExists(bool inGameMode)
    {
        // The desktop-to-game transition retracts and closes BEFORE steam://open/bigpicture
        // fires: Steam rebuilds its front-end for that request, and injected state left behind
        // stalled the gamepad UI bootstrap (device-diagnosed 2026-09-01).
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(true, inGameMode, SteamUiHold.BigPictureRequest, false));
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(true, inGameMode, SteamUiHold.BigPictureRequest, true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TransportShouldBeOpen_MasterSwitchOff_NeverOpens(bool inGameMode, bool bigPictureReady)
    {
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(false, inGameMode, SteamUiHold.None, bigPictureReady));
    }

    [Fact]
    public void AColdStartIsAdmittedOnlyOnceItsWindowExists()
    {
        var admission = new BigPictureAdmission();

        Assert.False(admission.Observe(Observation(100, false), false, Ticks(0)));
        Assert.True(admission.Observe(Observation(100, true), false, Ticks(1)));
    }

    [Fact]
    public void AnAdmittedWindowMissingBrieflyKeepsTheAdmission()
    {
        var admission = new BigPictureAdmission();
        admission.Observe(Observation(100, true), false, Ticks(0));

        Assert.True(admission.Observe(Observation(100, false), false, Ticks(1)));
        Assert.True(admission.Observe(Observation(100, false), false,
            Ticks(1) + Seconds(SteamUiReadiness.BigPictureLossGrace) - 1));
        Assert.True(admission.Observe(Observation(100, true), false, Ticks(10)));
    }

    [Fact]
    public void AWindowMissingForTheWholeGraceWithdrawsTheAdmission()
    {
        var admission = new BigPictureAdmission();
        admission.Observe(Observation(100, true), false, Ticks(0));
        admission.Observe(Observation(100, false), false, Ticks(1));

        Assert.False(admission.Observe(Observation(100, false), false,
            Ticks(1) + Seconds(SteamUiReadiness.BigPictureLossGrace)));
        Assert.False(admission.Observe(Observation(100, false), false, Ticks(5)));
    }

    [Fact]
    public void ANewBrowserProcessMustShowItsOwnWindow()
    {
        var admission = new BigPictureAdmission();
        admission.Observe(Observation(100, true), false, Ticks(0));

        Assert.False(admission.Observe(Observation(200, false), false, Ticks(1)));
    }

    [Fact]
    public void AWindowSeenDuringAHoldEarnsNoGrace()
    {
        var admission = new BigPictureAdmission();

        Assert.True(admission.Observe(Observation(100, true), true, Ticks(0)));
        Assert.False(admission.Observe(Observation(100, false), false, Ticks(1)));
    }

    private static SteamProcessSnapshot Observation(uint browser, bool window)
    {
        return new SteamProcessSnapshot(true, browser, true, window);
    }

    private static long Ticks(int seconds)
    {
        return 1_000 * Stopwatch.Frequency + seconds * Stopwatch.Frequency;
    }

    private static long Seconds(TimeSpan span)
    {
        return (long)(span.TotalSeconds * Stopwatch.Frequency);
    }

    [Fact]
    public async Task RunWhenReady_TransportAlreadyOpen_AttemptsAtOnce()
    {
        _readiness.Observe(true);

        var completed = await _readiness.RunWhenReadyAsync(
                "test", _ => Task.FromResult(true), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(completed);
    }

    [Fact]
    public async Task RunWhenReady_TransportClosed_WaitsForTheReadyEdge()
    {
        var attempts = 0;
        var run = _readiness.RunWhenReadyAsync(
            "test", _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(true);
            }, CancellationToken.None);

        Assert.False(run.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref attempts));

        _readiness.Observe(true);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task RunWhenReady_FailedAttempt_RunsAgainOnlyAtTheNextReadyEdge()
    {
        TaskCompletionSource firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        _readiness.Observe(true);
        var run = _readiness.RunWhenReadyAsync(
            "test", _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    firstAttempt.TrySetResult();
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            }, CancellationToken.None);
        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Still open: no edge, so nothing tries again.
        _readiness.Observe(true);
        Assert.False(run.IsCompleted);

        _readiness.Observe(false);
        _readiness.Observe(true);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task RunWhenReady_CancelledWhileWaiting_ReturnsFalseWithoutAnAttempt()
    {
        using CancellationTokenSource cancellation = new();
        var attempts = 0;
        var run = _readiness.RunWhenReadyAsync(
            "test", _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(true);
            }, cancellation.Token);

        await cancellation.CancelAsync();

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref attempts));
    }
}
