using System.Collections.Concurrent;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SessionShutdownTests
{
    [Fact]
    public void AThrowingAdmissionClosureCannotSkipLaterOwnerClosuresOrSafetyCleanup()
    {
        var failures = new ConcurrentQueue<Exception>();
        var failedClosure = new InvalidOperationException("device admission failed");
        List<string> completed = [];

        ShellSession.Step(failures, "device admission", () => throw failedClosure);
        ShellSession.Step(failures, "common admission", () => completed.Add("common admission"));
        ShellSession.Step(failures, "graphics admission", () => completed.Add("graphics admission"));
        ShellSession.Step(failures, "controller safety", () => completed.Add("controller safety"));

        Assert.Equal(["common admission", "graphics admission", "controller safety"], completed);
        Assert.Same(failedClosure, Assert.Single(failures));
    }

    [Fact]
    public async Task TightenedDeadlineReleasesTheJoinAndRetainsUnfinishedWork()
    {
        using var deadline = new CancellationTokenSource();
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joining = ShellSession.JoinWithinDeadlineAsync(owner.Task, deadline.Token, "device power transition");

        Assert.False(joining.IsCompleted);
        deadline.Cancel();

        var failure = await Assert.ThrowsAsync<TimeoutException>(() => joining);
        Assert.Contains("device power transition", failure.Message, StringComparison.Ordinal);
        Assert.False(owner.Task.IsCompleted);
        owner.SetResult();
        await owner.Task;
    }

    [Fact]
    public async Task CompletedCleanupIsStillObservedAfterTheDeadline()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();

        await ShellSession.JoinWithinDeadlineAsync(Task.CompletedTask, deadline.Token, "controller safety");
    }

    [Fact]
    public async Task FaultedCleanupIsReportedEvenAfterTheDeadline()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        var failure = new InvalidOperationException("controller release failed");

        var reported = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShellSession.JoinWithinDeadlineAsync(Task.FromException(failure), deadline.Token, "controller safety"));

        Assert.Same(failure, reported);
    }

    [Fact]
    public async Task OwnerCancellationRemainsDistinctFromDeadlineExpiry()
    {
        using var deadline = new CancellationTokenSource();
        using var ownerCancellation = new CancellationTokenSource();
        ownerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShellSession.JoinWithinDeadlineAsync(
            Task.FromCanceled(ownerCancellation.Token), deadline.Token, "startup"));
    }
}
