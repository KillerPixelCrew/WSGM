using WSGM.Shell;

namespace WSGM.Tests.Core;

public sealed class SteamUiTransportGateTests : IDisposable
{
    public SteamUiTransportGateTests()
    {
        // The ready edge is process-wide, as the gate that reports it is; every test starts closed.
        SteamUiReadiness.Observe(false);
    }

    public void Dispose()
    {
        SteamUiReadiness.Observe(false);
    }

    [Fact]
    public void TransportShouldBeOpen_GameModeWithoutBigPictureWindow_HoldsTheTransportClosed()
    {
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(
            true,
            true,
            false,
            false));
    }

    [Fact]
    public void TransportShouldBeOpen_GameModeWithBigPictureWindow_Opens()
    {
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(
            true,
            true,
            false,
            true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransportShouldBeOpen_DesktopMode_OpensOnTheMasterSwitchAlone(bool bigPictureReady)
    {
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(
            true,
            false,
            false,
            bigPictureReady));
    }

    [Fact]
    public void TransportShouldBeOpen_BigPictureRequestPendingInDesktopMode_HoldsTheTransportClosed()
    {
        // The desktop-to-game transition retracts and closes BEFORE steam://open/bigpicture
        // fires: Steam rebuilds its front-end for that request, and injected state left behind
        // stalled the gamepad UI bootstrap (device-diagnosed 2026-09-01).
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(
            true,
            false,
            true,
            false));
    }

    [Fact]
    public void TransportShouldBeOpen_BigPictureRequestPendingAndWindowUp_Opens()
    {
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(
            true,
            false,
            true,
            true));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TransportShouldBeOpen_BigPictureClosePending_HoldsTheTransportClosed(
        bool inGameMode,
        bool bigPictureReady)
    {
        // The mirror of the request hold. steam://close/bigpicture rebuilds Steam's front-end
        // back to the desktop client, and driving patches and evaluations through that rebuild
        // wedged steamwebhelper for three minutes (Claw, 2026-09-26). The hold covers the whole
        // desktop return, including the window still being up as the request is dispatched.
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(
            true,
            inGameMode,
            false,
            bigPictureReady,
            true));
    }

    [Fact]
    public void TransportShouldBeOpen_BigPictureCloseSettled_ReopensInDesktopMode()
    {
        Assert.True(SteamUiReadiness.TransportShouldBeOpen(
            true,
            false,
            false,
            false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TransportShouldBeOpen_MasterSwitchOff_NeverOpens(bool inGameMode, bool bigPictureReady)
    {
        Assert.False(SteamUiReadiness.TransportShouldBeOpen(
            false,
            inGameMode,
            false,
            bigPictureReady));
    }

    [Fact]
    public async Task RunWhenReady_TransportAlreadyOpen_AttemptsAtOnce()
    {
        SteamUiReadiness.Observe(true);

        var completed = await SteamUiReadiness.RunWhenReadyAsync(
                "test", _ => Task.FromResult(true), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(completed);
    }

    [Fact]
    public async Task RunWhenReady_TransportClosed_WaitsForTheReadyEdge()
    {
        var attempts = 0;
        var run = SteamUiReadiness.RunWhenReadyAsync(
            "test", _ =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(true);
            }, CancellationToken.None);

        Assert.False(run.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref attempts));

        SteamUiReadiness.Observe(true);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task RunWhenReady_FailedAttempt_RunsAgainOnlyAtTheNextReadyEdge()
    {
        TaskCompletionSource firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        SteamUiReadiness.Observe(true);
        var run = SteamUiReadiness.RunWhenReadyAsync(
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
        SteamUiReadiness.Observe(true);
        Assert.False(run.IsCompleted);

        SteamUiReadiness.Observe(false);
        SteamUiReadiness.Observe(true);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task RunWhenReady_CancelledWhileWaiting_ReturnsFalseWithoutAnAttempt()
    {
        using CancellationTokenSource cancellation = new();
        var attempts = 0;
        var run = SteamUiReadiness.RunWhenReadyAsync(
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
