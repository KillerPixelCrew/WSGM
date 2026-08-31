using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests;

public sealed class ApplicationShutdownTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    public void ProcessExitCodeReportsIncompleteShutdown(int outcome, int expected)
    {
        Assert.Equal(expected, ApplicationShutdownCoordinator.ExitCodeFor(
            (ApplicationShutdownOutcome)outcome));
    }

    [Fact]
    public void BudgetsMatchFrozenShutdownAndUpdatePreStopDeadlines()
    {
        Assert.Equal(TimeSpan.FromSeconds(15),
            ApplicationShutdownCoordinator.BudgetFor(ApplicationShutdownReason.Normal));
        Assert.Equal(TimeSpan.FromSeconds(10),
            ApplicationShutdownCoordinator.BudgetFor(ApplicationShutdownReason.Update));
        Assert.Equal(TimeSpan.FromSeconds(5),
            ApplicationShutdownCoordinator.BudgetFor(ApplicationShutdownReason.SessionEnd));
        Assert.Equal(TimeSpan.FromSeconds(20),
            ApplicationShutdownCoordinator.BudgetFor(ApplicationShutdownReason.Uninstall));
        Assert.Equal(TimeSpan.FromSeconds(10), Steam.UpdateStopBudget);
    }

    [Fact]
    public async Task CompletedCleanupReturnsClean()
    {
        ApplicationShutdownOutcome outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            static _ => ValueTask.CompletedTask,
            ApplicationShutdownReason.Normal,
            TimeSpan.FromSeconds(1));

        Assert.Equal(ApplicationShutdownOutcome.Clean, outcome);
    }

    [Fact]
    public async Task CleanupThatStartedButFaultedReturnsUnverified()
    {
        ApplicationShutdownOutcome outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            static _ => ValueTask.FromException(new InvalidOperationException("fault")),
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1));

        Assert.Equal(ApplicationShutdownOutcome.Unverified, outcome);
    }

    [Fact]
    public async Task CleanupTimeoutExceptionIsNotMistakenForTheOuterDeadline()
    {
        ApplicationShutdownOutcome outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            static _ => ValueTask.FromException(new TimeoutException("subsystem timeout")),
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1));

        Assert.Equal(ApplicationShutdownOutcome.Unverified, outcome);
    }

    [Fact]
    public async Task CleanupThatCouldNotStartReturnsFailed()
    {
        ApplicationShutdownOutcome outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            static _ => throw new InvalidOperationException("could not start"),
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1));

        Assert.Equal(ApplicationShutdownOutcome.Failed, outcome);
    }

    [Fact]
    public async Task HungCleanupReturnsTimedOutAtTheOuterBoundary()
    {
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ApplicationShutdownOutcome outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            _ => new ValueTask(neverCompletes.Task),
            ApplicationShutdownReason.SessionEnd,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(ApplicationShutdownOutcome.TimedOut, outcome);
    }

    [Fact]
    public void InstallerExitCannotBeDowngradedBySessionEnd()
    {
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Update);
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Normal);

        Assert.Equal(ApplicationShutdownReason.Update, ApplicationShutdownRequest.Consume());
        Assert.Equal(ApplicationShutdownReason.Normal, ApplicationShutdownRequest.Consume());
    }

    [Fact]
    public void UninstallRemainsTheStrongestExitRequest()
    {
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Uninstall);
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Update);

        Assert.Equal(ApplicationShutdownReason.Uninstall, ApplicationShutdownRequest.Consume());
    }

    [Fact]
    public async Task OuterDeadlineIsPassedToTheShutdownOwner()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        DateTimeOffset received = default;

        ApplicationShutdownOutcome outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            deadline =>
            {
                received = deadline;
                return ValueTask.CompletedTask;
            },
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1));

        Assert.Equal(ApplicationShutdownOutcome.Clean, outcome);
        Assert.InRange(received, before.AddMilliseconds(900), before.AddSeconds(2));
    }

    [Fact]
    public async Task SynchronousShutdownStartupConsumesTheSameOuterBudget()
    {
        DateTimeOffset started = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        Queue<DateTimeOffset> clock = new(
        [
            started,
            started.AddSeconds(2),
        ]);
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timerStarted = false;

        ApplicationShutdownOutcome outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            _ => new ValueTask(neverCompletes.Task),
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1),
            () => clock.Dequeue(),
            _ =>
            {
                timerStarted = true;
                return Task.CompletedTask;
            });

        Assert.Equal(ApplicationShutdownOutcome.TimedOut, outcome);
        Assert.False(timerStarted);
    }

    [Fact]
    public async Task SynchronouslyCompletedCleanupCannotOutrunTheOuterBudget()
    {
        DateTimeOffset started = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        Queue<DateTimeOffset> clock = new(
        [
            started,
            started.AddSeconds(2),
        ]);

        ApplicationShutdownOutcome outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            static _ => ValueTask.CompletedTask,
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1),
            () => clock.Dequeue(),
            static _ => throw new InvalidOperationException(
                "A timer is unnecessary after the deadline."));

        Assert.Equal(ApplicationShutdownOutcome.TimedOut, outcome);
    }

    [Fact]
    public async Task RetainedShutdownFailurePropagatesAsApplicationShutdownUnverified()
    {
        // ShellSession.ShutdownAsync completes its remaining cleanup and then reports the
        // retained failures as one exception; the coordinator must record that as Unverified.
        bool cleanupRan = false;

        ApplicationShutdownOutcome outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            async _ =>
            {
                cleanupRan = true;
                await Task.Yield();
                throw new InvalidOperationException(
                    "Application shutdown completed its remaining cleanup, but one or more steps were unverified.",
                    new InvalidOperationException("device release unverified"));
            },
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1));

        Assert.True(cleanupRan);
        Assert.Equal(ApplicationShutdownOutcome.Unverified, outcome);
    }
}
