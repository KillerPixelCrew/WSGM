using WSGM.Core;

namespace WSGM.Tests.Core;

public sealed class ExplorerLauncherTests
{
    [Fact]
    public async Task CancellationAfterARefusedSchedulePreventsTheDirectStart()
    {
        using CancellationTokenSource cancellation = new();
        var starts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExplorerLauncher.StartAsync(true, _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(ScheduledTaskLaunchDisposition.NotDispatched);
        }, () =>
        {
            starts++;
            return ScheduledTaskLaunchDisposition.Dispatched;
        }, cancellation.Token));
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(false, (int)ScheduledTaskLaunchDisposition.Dispatched, 0, 1)]
    [InlineData(true, (int)ScheduledTaskLaunchDisposition.Dispatched, 1, 0)]
    [InlineData(true, (int)ScheduledTaskLaunchDisposition.NotDispatched, 1, 1)]
    [InlineData(true, (int)ScheduledTaskLaunchDisposition.Unknown, 1, 0)]
    public async Task SchedulingCertaintyControlsWhetherAnotherProcessCanStart(bool elevated, int scheduledOutcome,
        int expectedSchedules, int expectedStarts)
    {
        var schedules = 0;
        var starts = 0;
        await ExplorerLauncher.StartAsync(elevated, _ =>
        {
            schedules++;
            return Task.FromResult((ScheduledTaskLaunchDisposition)scheduledOutcome);
        }, () =>
        {
            starts++;
            return ScheduledTaskLaunchDisposition.Dispatched;
        }, CancellationToken.None);
        Assert.Equal(expectedSchedules, schedules);
        Assert.Equal(expectedStarts, starts);
    }
}
