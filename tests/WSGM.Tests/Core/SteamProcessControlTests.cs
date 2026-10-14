extern alias wsgm;
using SteamProcessControl = wsgm::WSGM.Shared.SteamProcessControl;

namespace WSGM.Tests.Core;

public sealed class SteamProcessControlTests
{
    [Fact]
    public async Task AlreadyClosedSteamNeedsNoExitRequest()
    {
        var harness = new Harness { Running = false };

        Assert.True(await harness.StopAsync());
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task OrderlyExitAvoidsForcedTermination()
    {
        var harness = new Harness();
        harness.OnDelay = () => harness.Running = false;

        Assert.True(await harness.StopAsync());
        Assert.Equal(["exit"], harness.Calls);
    }

    [Fact]
    public async Task IgnoredExitIsForcedAfterItsGraceAndVerified()
    {
        var harness = new Harness();
        harness.OnForce = () => harness.Running = false;

        Assert.True(await harness.StopAsync());
        Assert.Equal(["exit", "force"], harness.Calls);
        Assert.Equal(TimeSpan.FromSeconds(1), harness.ForceAt);
    }

    [Fact]
    public async Task FailedExitDispatchForcesImmediately()
    {
        var harness = new Harness { ExitAccepted = false };
        harness.OnForce = () => harness.Running = false;

        Assert.True(await harness.StopAsync());
        Assert.Equal(["exit", "force"], harness.Calls);
        Assert.Equal(TimeSpan.Zero, harness.ForceAt);
    }

    [Fact]
    public async Task RemainingSteamHelpersPreventASuccessfulStop()
    {
        var harness = new Harness();

        Assert.False(await harness.StopAsync());
        Assert.Equal(["exit", "force"], harness.Calls);
        Assert.Equal(TimeSpan.FromSeconds(3), harness.Now);
    }

    [Fact]
    public async Task ForcedExitMaySettleWithinTheRemainingBudget()
    {
        var harness = new Harness();
        harness.OnDelay = () =>
        {
            if (harness.ForceAt is not null)
            {
                harness.Running = false;
            }
        };

        Assert.True(await harness.StopAsync());
        Assert.Equal(["exit", "force"], harness.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), harness.Now);
    }

    [Fact]
    public async Task CancellationBeforeARequestPreventsAllEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var harness = new Harness();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.StopAsync(cancellation.Token));
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task CancellationDuringGracePreventsALaterHardKill()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new Harness { OnDelay = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.StopAsync(cancellation.Token));
        Assert.Equal(["exit"], harness.Calls);
    }

    [Fact]
    public async Task ALongSuspendConsumesTheBudgetWithoutALateHardKill()
    {
        var harness = new Harness();
        harness.OnDelay = () => harness.Now += TimeSpan.FromHours(1);

        Assert.False(await harness.StopAsync());
        Assert.Equal(["exit"], harness.Calls);
    }

    private sealed class Harness
    {
        internal bool Running { get; set; } = true;
        internal bool ExitAccepted { get; init; } = true;
        internal TimeSpan Now { get; set; }
        internal TimeSpan? ForceAt { get; private set; }
        internal List<string> Calls { get; } = [];
        internal Action? OnDelay { get; set; }
        internal Action? OnForce { get; set; }

        internal Task<bool> StopAsync(CancellationToken cancellationToken = default)
        {
            return SteamProcessControl.StopAsync(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1),
                () => Running,
                () =>
                {
                    Calls.Add("exit");
                    return ExitAccepted;
                },
                () =>
                {
                    Calls.Add("force");
                    ForceAt = Now;
                    OnForce?.Invoke();
                },
                () => Now,
                (delay, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Now += delay;
                    OnDelay?.Invoke();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                cancellationToken);
        }
    }
}
