using WSGM.Core;

namespace WSGM.Tests.Core;

/// <summary>Task XML generation for the de-elevating scheduled-task launcher.</summary>
public sealed class UnelevatedLauncherTests
{
    [Fact]
    public async Task CancellationWhileCreateIsUnobservableStillDeletesOnce()
    {
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        using CancellationTokenSource cancellation = new();
        List<string> calls = [];
        (DateTimeOffset Deadline, CancellationToken Token)? cleanup = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UnelevatedLauncher.RunScheduledTaskSequenceAsync("WSGM_Test", @"C:\fixture.xml", now.AddSeconds(1),
                (arguments, deadline, token) =>
                {
                    calls.Add(arguments);
                    if (arguments.StartsWith("/Create", StringComparison.Ordinal))
                    {
                        cancellation.Cancel();
                        return Task.FromCanceled<ConsoleToolRunOutcome>(cancellation.Token);
                    }

                    cleanup = (deadline, token);
                    return Task.FromResult(ConsoleToolRunOutcome.Succeeded);
                }, () => now, cancellation.Token));
        Assert.Equal(2, calls.Count);
        Assert.StartsWith("/Delete", calls[1], StringComparison.Ordinal);
        Assert.Equal(now.AddSeconds(5), cleanup?.Deadline);
        Assert.Equal(CancellationToken.None, cleanup?.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureAfterBudgetClosureDoesNotReplaceTheDispatchOutcome(bool unknown)
    {
        var runOutcome = unknown ? ConsoleToolRunOutcome.Unknown : ConsoleToolRunOutcome.Succeeded;
        var expected = unknown ? ScheduledTaskLaunchDisposition.Unknown : ScheduledTaskLaunchDisposition.Dispatched;
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var deadline = now.AddSeconds(1);
        List<string> calls = [];
        (DateTimeOffset Deadline, CancellationToken Token)? cleanup = null;
        var result = await UnelevatedLauncher.RunScheduledTaskSequenceAsync("WSGM_Test", @"C:\fixture.xml", deadline,
            (arguments, commandDeadline, token) =>
            {
                calls.Add(arguments);
                if (arguments.StartsWith("/Run", StringComparison.Ordinal))
                {
                    now = deadline;
                    return Task.FromResult(runOutcome);
                }

                if (arguments.StartsWith("/Delete", StringComparison.Ordinal))
                {
                    cleanup = (commandDeadline, token);
                    throw new IOException("fake cleanup failure");
                }

                return Task.FromResult(ConsoleToolRunOutcome.Succeeded);
            }, () => now, CancellationToken.None);
        Assert.Equal(expected, result);
        Assert.Equal(3, calls.Count);
        Assert.Single(calls, call => call.StartsWith("/Delete", StringComparison.Ordinal));
        Assert.Equal(deadline.AddSeconds(5), cleanup?.Deadline);
        Assert.Equal(CancellationToken.None, cleanup?.Token);
    }

    [Fact]
    public void DeElevationTaskEscapesExecutableAndArgumentsInXml()
    {
        var xml = UnelevatedLauncher.BuildTaskXml(@"C:\A&B\WSGM.exe", "--open-<wifi>-settings", @"C:\A&B");

        Assert.Contains(@"<Command>C:\A&amp;B\WSGM.exe</Command>", xml);
        Assert.Contains("<Arguments>--open-&lt;wifi&gt;-settings</Arguments>", xml);
        Assert.Contains("<WorkingDirectory>C:\\A&amp;B</WorkingDirectory>", xml);
    }

    [Fact]
    public void DeElevationTaskUsesInteractiveTokenWithoutAnElevatedRunLevelInUtf16()
    {
        // The three properties invariant 5 rests on: an InteractiveToken principal with
        // NO RunLevel element yields the user's filtered medium-IL token (a RunLevel of
        // HighestAvailable would hand Explorer and the ms-settings one-shot back their
        // elevation), and schtasks rejects anything but the UTF-16 declaration with
        // "cannot switch encoding".
        var xml = UnelevatedLauncher.BuildTaskXml(@"C:\WSGM\WSGM.exe");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.DoesNotContain("<RunLevel>", xml);
        Assert.DoesNotContain("<Arguments>", xml);
        Assert.DoesNotContain("<WorkingDirectory>", xml);
    }

    [Fact]
    public async Task ScheduledTaskRunFailure_DeletesWithinTheSameAbsoluteDeadline()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        using var cancellation = new CancellationTokenSource();
        var calls = new List<(string Arguments, DateTimeOffset Deadline, CancellationToken Token)>();

        var disposition =
            await UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                RunCommand,
                cancellation.Token);

        Assert.Equal(ScheduledTaskLaunchDisposition.NotDispatched, disposition);
        Assert.Collection(
            calls,
            call => Assert.StartsWith("/Create", call.Arguments, StringComparison.Ordinal),
            call => Assert.StartsWith("/Run", call.Arguments, StringComparison.Ordinal),
            call => Assert.StartsWith("/Delete", call.Arguments, StringComparison.Ordinal));
        Assert.All(calls, call => Assert.Equal(deadline, call.Deadline));
        Assert.All(calls, call => Assert.Equal(cancellation.Token, call.Token));

        return;

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            calls.Add((arguments, commandDeadline, cancellationToken));
            return Task.FromResult(arguments.StartsWith("/Run", StringComparison.Ordinal)
                ? ConsoleToolRunOutcome.Failed
                : ConsoleToolRunOutcome.Succeeded);
        }
    }

    [Fact]
    public async Task ScheduledTaskRunTimeout_PreservesUnknownDispatchBoundary()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        var calls = new List<string>();

        var disposition =
            await UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                RunCommand,
                CancellationToken.None);

        Assert.Equal(ScheduledTaskLaunchDisposition.Unknown, disposition);
        Assert.Collection(
            calls,
            call => Assert.StartsWith("/Create", call, StringComparison.Ordinal),
            call => Assert.StartsWith("/Run", call, StringComparison.Ordinal),
            call => Assert.StartsWith("/Delete", call, StringComparison.Ordinal));

        return;

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            _ = commandDeadline;
            _ = cancellationToken;
            calls.Add(arguments);
            return Task.FromResult(arguments.StartsWith("/Run", StringComparison.Ordinal)
                ? ConsoleToolRunOutcome.Unknown
                : ConsoleToolRunOutcome.Succeeded);
        }
    }

    [Fact]
    public async Task ScheduledTaskCreateUnknown_AttemptsCleanupWithoutDispatching()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        var calls = new List<string>();

        var disposition =
            await UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                RunCommand,
                CancellationToken.None);

        Assert.Equal(ScheduledTaskLaunchDisposition.NotDispatched, disposition);
        Assert.Collection(
            calls,
            call => Assert.StartsWith("/Create", call, StringComparison.Ordinal),
            call => Assert.StartsWith("/Delete", call, StringComparison.Ordinal));

        return;

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            _ = commandDeadline;
            _ = cancellationToken;
            calls.Add(arguments);
            return Task.FromResult(arguments.StartsWith("/Create", StringComparison.Ordinal)
                ? ConsoleToolRunOutcome.Unknown
                : ConsoleToolRunOutcome.Succeeded);
        }
    }

    [Fact]
    public async Task ScheduledTaskDeadlineClosesAfterCreate_SkipsRunAndStillDeletes()
    {
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var deadline = now + TimeSpan.FromSeconds(1);
        var calls = new List<string>();
        (DateTimeOffset Deadline, CancellationToken Token)? cleanup = null;

        var disposition =
            await UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                RunCommand,
                () => now,
                CancellationToken.None);

        Assert.Equal(ScheduledTaskLaunchDisposition.NotDispatched, disposition);
        Assert.Collection(calls,
            call => Assert.StartsWith("/Create", call, StringComparison.Ordinal),
            call => Assert.StartsWith("/Delete", call, StringComparison.Ordinal));
        Assert.Equal(deadline.AddSeconds(5), cleanup?.Deadline);
        Assert.Equal(CancellationToken.None, cleanup?.Token);

        return;

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            calls.Add(arguments);
            if (arguments.StartsWith("/Delete", StringComparison.Ordinal))
            {
                cleanup = (commandDeadline, cancellationToken);
            }
            else
            {
                Assert.Equal(deadline, commandDeadline);
            }

            now = deadline;
            return Task.FromResult(ConsoleToolRunOutcome.Succeeded);
        }
    }

    [Fact]
    public async Task ScheduledTaskCancellationAfterCreate_SkipsRunAndStillDeletes()
    {
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var deadline = now + TimeSpan.FromSeconds(1);
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        (DateTimeOffset Deadline, CancellationToken Token)? cleanup = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UnelevatedLauncher.RunScheduledTaskSequenceAsync(
                "WSGM_Test",
                @"C:\safe-test-task.xml",
                deadline,
                RunCommand,
                () => now,
                cancellation.Token));

        Assert.Collection(calls,
            call => Assert.StartsWith("/Create", call, StringComparison.Ordinal),
            call => Assert.StartsWith("/Delete", call, StringComparison.Ordinal));
        Assert.Equal(now.AddSeconds(5), cleanup?.Deadline);
        Assert.Equal(CancellationToken.None, cleanup?.Token);

        return;

        Task<ConsoleToolRunOutcome> RunCommand(
            string arguments,
            DateTimeOffset commandDeadline,
            CancellationToken cancellationToken)
        {
            calls.Add(arguments);
            if (arguments.StartsWith("/Create", StringComparison.Ordinal))
            {
                Assert.Equal(cancellation.Token, cancellationToken);
                Assert.Equal(deadline, commandDeadline);
                cancellation.Cancel();
            }
            else
            {
                cleanup = (commandDeadline, cancellationToken);
            }

            return Task.FromResult(ConsoleToolRunOutcome.Succeeded);
        }
    }
}
