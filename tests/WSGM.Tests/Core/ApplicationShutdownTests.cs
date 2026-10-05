using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Tests.Core;

public sealed class ApplicationShutdownTests : IDisposable
{
    public ApplicationShutdownTests()
    {
        ApplicationShutdownRequest.ResetForTests();
    }

    public void Dispose()
    {
        ApplicationShutdownRequest.ResetForTests();
    }

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
    public async Task CompletedCleanupReturnsClean()
    {
        var outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            static _ => ValueTask.CompletedTask,
            ApplicationShutdownReason.Normal,
            TimeSpan.FromSeconds(1));

        Assert.Equal(ApplicationShutdownOutcome.Clean, outcome);
    }

    [Fact]
    public async Task CleanupThatStartedButFaultedReturnsUnverified()
    {
        var outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            static _ => ValueTask.FromException(new InvalidOperationException("fault")),
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1));

        Assert.Equal(ApplicationShutdownOutcome.Unverified, outcome);
    }

    [Fact]
    public async Task CleanupTimeoutExceptionIsNotMistakenForTheOuterDeadline()
    {
        var outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            static _ => ValueTask.FromException(new TimeoutException("subsystem timeout")),
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1));

        Assert.Equal(ApplicationShutdownOutcome.Unverified, outcome);
    }

    [Fact]
    public async Task CleanupThatCouldNotStartReturnsFailed()
    {
        var outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
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

        var outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
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

        Assert.Equal(ApplicationShutdownReason.Update, ApplicationShutdownRequest.Current);
        Assert.True(ApplicationShutdownRequest.SessionEnding);
        Assert.Equal(ApplicationShutdownReason.Update, ApplicationShutdownRequest.Current);
    }

    [Fact]
    public void UninstallRemainsTheStrongestExitRequest()
    {
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Uninstall);
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Update);

        Assert.Equal(ApplicationShutdownReason.Uninstall, ApplicationShutdownRequest.Current);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RuntimeRunsCleanupBeforeOneHandoffAndOneForcedExit(int requestedReason)
    {
        var reason = (ApplicationShutdownReason)requestedReason;
        ApplicationShutdownRequest.Request(reason);
        List<string> order = [];
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ApplicationRuntime runtime = new(async (received, _) =>
        {
            Assert.Equal(reason, received);
            order.Add("cleanup");
            await held.Task;
        }, code => order.Add($"exit:{code}"), (received, outcome) =>
        {
            Assert.Equal(reason, received);
            Assert.Equal(ApplicationShutdownOutcome.Clean, outcome);
            order.Add("handoff");
        });

        var exit = runtime.RequestExit();
        Assert.Same(exit, runtime.RequestExit());
        Assert.Equal(["cleanup"], order);
        held.SetResult();
        await exit;
        Assert.Same(exit, runtime.RequestExit());
        Assert.Equal(["cleanup", "handoff", "exit:0"], order);
    }

    [Fact]
    public async Task SynchronousCleanupCanReenterWithoutStartingASecondExit()
    {
        var calls = 0;
        Task? reentered = null;
        ApplicationRuntime? runtime = null;
        runtime = new ApplicationRuntime((_, _) =>
        {
            calls++;
            reentered = runtime!.RequestExit();
            return ValueTask.CompletedTask;
        }, _ => { }, (_, _) => { });

        var exit = runtime.RequestExit();
        await exit;

        Assert.Same(exit, reentered);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RuntimeWithoutASessionCompletesTheInstallerHandoffAndExitsCleanly()
    {
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Update);
        List<(ApplicationShutdownReason, ApplicationShutdownOutcome)> handoffs = [];
        List<int> codes = [];
        ApplicationRuntime runtime = new(null, codes.Add, (reason, outcome) => handoffs.Add((reason, outcome)));

        await runtime.RequestExit();
        await runtime.RequestExit();

        Assert.Equal([(ApplicationShutdownReason.Update, ApplicationShutdownOutcome.Clean)], handoffs);
        Assert.Equal([0], codes);
    }

    [Fact]
    public async Task StartupFailureKeepsExitCodeOneAfterSuccessfulCleanup()
    {
        var cleanups = 0;
        List<int> codes = [];
        var reports = 0;
        ApplicationRuntime runtime = new((_, _) =>
        {
            cleanups++;
            return ValueTask.CompletedTask;
        }, codes.Add, (_, _) => reports++);

        await runtime.StartupFailedExit();

        Assert.True(runtime.StartupFailed);
        Assert.Equal(1, cleanups);
        Assert.Equal(1, reports);
        Assert.Equal([1], codes);
    }

    [Fact]
    public async Task OsSessionEndOwnsTheExitAndRunsOneSessionEndCleanup()
    {
        List<ApplicationShutdownReason> cleanups = [];
        var forcedExits = 0;
        ApplicationRuntime runtime = new((reason, _) =>
        {
            cleanups.Add(reason);
            return ValueTask.CompletedTask;
        }, _ => forcedExits++, (_, _) => { });

        await runtime.RequestOsSessionEnd();

        Assert.Equal([ApplicationShutdownReason.SessionEnd], cleanups);
        Assert.True(ApplicationShutdownRequest.SessionEnding);
        Assert.Equal(0, forcedExits);
    }

    [Fact]
    public async Task SessionEndDuringNormalCleanupTightensDeadlineAndSuppressesDesktopStepsAndForcedExit()
    {
        DateTimeOffset now = new(2026, 10, 4, 1, 0, 0, TimeSpan.Zero);
        var started = now;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var explorerStarts = 0;
        var bigPictureCloses = 0;
        var forcedExits = 0;
        ApplicationRuntime runtime = new(async (_, _) =>
        {
            await held.Task;
            if (!ApplicationShutdownRequest.SessionEnding)
            {
                bigPictureCloses++;
                explorerStarts++;
            }
        }, _ => forcedExits++, (_, _) => { }, () => now);

        var exit = runtime.RequestExit();
        Assert.Equal(started.AddSeconds(15), runtime.Deadline);
        now = now.AddSeconds(2);
        Assert.Same(exit, runtime.RequestOsSessionEnd());
        Assert.Equal(started.AddSeconds(7), runtime.Deadline);
        now = now.AddSeconds(1);
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Normal);
        Assert.Same(exit, runtime.RequestExit());
        Assert.Equal(started.AddSeconds(7), runtime.Deadline);
        held.SetResult();
        await exit;

        Assert.Equal(0, explorerStarts);
        Assert.Equal(0, bigPictureCloses);
        Assert.Equal(0, forcedExits);
    }

    [Fact]
    public async Task UpdateDuringNormalCleanupReportsTheFinalReasonOnce()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<(ApplicationShutdownReason, ApplicationShutdownOutcome)> handoffs = [];
        ApplicationRuntime runtime = new((_, _) => new ValueTask(held.Task), _ => { },
            (reason, outcome) => handoffs.Add((reason, outcome)));
        var exit = runtime.RequestExit();

        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Update);
        Assert.Same(exit, runtime.RequestExit());
        held.SetResult();
        await exit;

        Assert.Equal([(ApplicationShutdownReason.Update, ApplicationShutdownOutcome.Clean)], handoffs);
    }

    [Fact]
    public async Task SessionEndStillTightensAnUninstallWithoutDowngradingItsHandoffReason()
    {
        DateTimeOffset now = new(2026, 10, 4, 1, 0, 0, TimeSpan.Zero);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ApplicationShutdownRequest.Request(ApplicationShutdownReason.Uninstall);
        ApplicationRuntime runtime = new((_, _) => new ValueTask(held.Task), _ => { }, (_, _) => { }, () => now);
        var exit = runtime.RequestExit();
        var original = runtime.Deadline;

        ApplicationShutdownRequest.Request(ApplicationShutdownReason.SessionEnd);
        _ = runtime.RequestExit();

        Assert.True(ApplicationShutdownRequest.SessionEnding);
        Assert.Equal(ApplicationShutdownReason.Uninstall, ApplicationShutdownRequest.Current);
        Assert.Equal(original.AddSeconds(-15), runtime.Deadline);
        held.SetResult();
        await exit;
    }

    [Fact]
    public async Task CleanupCompletingAfterTheTightenedDeadlineIsNotReportedClean()
    {
        DateTimeOffset now = new(2026, 10, 4, 1, 0, 0, TimeSpan.Zero);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ApplicationShutdownOutcome? reported = null;
        ApplicationRuntime runtime = new((_, _) => new ValueTask(held.Task), _ => { },
            (_, outcome) => reported = outcome, () => now);
        var exit = runtime.RequestExit();
        _ = runtime.RequestOsSessionEnd();
        now = now.AddSeconds(6);
        held.SetResult();
        await exit;

        Assert.Equal(ApplicationShutdownOutcome.TimedOut, reported);
    }

    [Fact]
    public async Task OuterDeadlineIsPassedToTheShutdownOwner()
    {
        var before = DateTimeOffset.UtcNow;
        DateTimeOffset received = default;

        var outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
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
            started.AddSeconds(2)
        ]);
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timerStarted = false;

        var outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            _ => new ValueTask(neverCompletes.Task),
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1),
            clock.Dequeue,
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
            started.AddSeconds(2)
        ]);

        var outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
            static _ => ValueTask.CompletedTask,
            ApplicationShutdownReason.Update,
            TimeSpan.FromSeconds(1),
            clock.Dequeue,
            static _ => throw new InvalidOperationException(
                "A timer is unnecessary after the deadline."));

        Assert.Equal(ApplicationShutdownOutcome.TimedOut, outcome);
    }

    [Fact]
    public async Task RetainedShutdownFailurePropagatesAsApplicationShutdownUnverified()
    {
        // ShellSession.ShutdownAsync completes its remaining cleanup and then reports the
        // retained failures as one exception; the coordinator must record that as Unverified.
        var cleanupRan = false;

        var outcome = await ApplicationShutdownCoordinator.ShutdownAsync(
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

    [Fact]
    public void VerifiedShutdownReportsNothing()
    {
        Assert.Null(ShellSession.ShutdownFailure([]));
    }

    [Fact]
    public void ASingleUnverifiedStepIsReportedAsItselfRatherThanBuriedInAnAggregate()
    {
        var deviceFailure = new InvalidOperationException("device release unverified");

        var reported = ShellSession.ShutdownFailure([deviceFailure]);

        Assert.IsType<InvalidOperationException>(reported);
        Assert.Same(deviceFailure, reported.InnerException);
    }

    [Fact]
    public void EveryUnverifiedStepSurvivesIntoTheReportedAggregate()
    {
        var device = new InvalidOperationException("device release unverified");
        var explorer = new IOException("explorer restore unverified");

        var reported = ShellSession.ShutdownFailure([device, explorer]);

        var aggregate = Assert.IsType<AggregateException>(reported!.InnerException);
        Assert.Equal([device, explorer], aggregate.InnerExceptions);
    }

    [Fact]
    public void SteamPreStopFailureStillRequestsUpdateCleanupAndLifetimeShutdown()
    {
        List<string> order = [];

        Program.RunInstallerExitRequest(
            ApplicationShutdownReason.Update,
            () =>
            {
                order.Add("steam");
                throw new InvalidOperationException("stop failed");
            },
            reason => order.Add($"request:{reason}"),
            () => order.Add("shutdown"));

        Assert.Equal(["steam", "request:Update", "shutdown"], order);
    }

    [Fact]
    public void UninstallRequestDoesNotStopSteam()
    {
        var steamStops = 0;
        var requested = ApplicationShutdownReason.Normal;
        var lifetimeStopped = false;

        Program.RunInstallerExitRequest(
            ApplicationShutdownReason.Uninstall,
            () => steamStops++,
            reason => requested = reason,
            () => lifetimeStopped = true);

        Assert.Equal(0, steamStops);
        Assert.Equal(ApplicationShutdownReason.Uninstall, requested);
        Assert.True(lifetimeStopped);
    }
}
