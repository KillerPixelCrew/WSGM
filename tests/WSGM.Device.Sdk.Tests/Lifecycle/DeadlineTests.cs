using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Device.Sdk.Tests.Lifecycle;

/// <summary>
///     The deadline every lifecycle context and command carries. It must not expire because the
///     process was frozen: on 2026-09-22 and 2026-09-28 a suspend mid-flight at sleep came back with a
///     wall-clock deadline already spent and failed on a machine that had just woken.
/// </summary>
public sealed class DeadlineTests
{
    [Fact]
    public void AFreezeCountsAsOneStepAtMost()
    {
        Assert.Equal(ActiveClock.MaximumStep, ActiveClock.CountedStep(TimeSpan.FromMinutes(61)));
        Assert.Equal(ActiveClock.MaximumStep, ActiveClock.CountedStep(TimeSpan.FromHours(9)));
    }

    [Fact]
    public void OrdinaryStepsCountInFull()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), ActiveClock.CountedStep(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(ActiveClock.MaximumStep, ActiveClock.CountedStep(ActiveClock.MaximumStep));
    }

    [Fact]
    public void ABackwardStepCountsAsNothing()
    {
        Assert.Equal(TimeSpan.Zero, ActiveClock.CountedStep(TimeSpan.FromMilliseconds(-5)));
    }

    [Fact]
    public void RemainingNeverGoesNegativeAndTheEndsBehave()
    {
        Assert.Equal(TimeSpan.Zero, Deadline.Expired.Remaining);
        Assert.True(Deadline.Expired.HasExpired);
        Assert.False(Deadline.Never.HasExpired);
        Assert.Equal(TimeSpan.MaxValue, Deadline.Never.Remaining);
        Assert.True(Deadline.After(TimeSpan.FromSeconds(-3)).HasExpired);
        Assert.InRange(Deadline.After(TimeSpan.FromSeconds(30)).Remaining, TimeSpan.FromSeconds(29),
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void EarliestAndOrderingAgree()
    {
        var soon = Deadline.After(TimeSpan.FromSeconds(1));
        var later = Deadline.After(TimeSpan.FromSeconds(60));

        Assert.Equal(soon, soon.Earliest(later));
        Assert.Equal(soon, later.Earliest(soon));
        Assert.True(soon < later);
        Assert.True(Deadline.Expired < soon && later < Deadline.Never);
    }

    [Fact]
    public void AnExpiredDeadlineHandsOutACancelledSource()
    {
        using var source = Deadline.Expired.CreateCancellationSource();

        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public async Task TheActiveClockCancelsAShortDeadlineOnTime()
    {
        using var source = Deadline.After(TimeSpan.FromMilliseconds(40)).CreateCancellationSource();

        await Task.Delay(TimeSpan.FromSeconds(2), source.Token).ContinueWith(_ => { }, TaskScheduler.Default);

        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public void ALinkedTokenStillCancels()
    {
        using var caller = new CancellationTokenSource();
        using var source = Deadline.Never.CreateCancellationSource(caller.Token);

        caller.Cancel();

        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public void AWallClockDeadlineConvertsFromNow()
    {
        Assert.True(Deadline.At(DateTimeOffset.UtcNow.AddSeconds(-1)).HasExpired);
        Assert.InRange(Deadline.At(DateTimeOffset.UtcNow.AddSeconds(10)).Remaining, TimeSpan.FromSeconds(9),
            TimeSpan.FromSeconds(10));
    }
}
