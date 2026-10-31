using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class SessionShutdownTests
{
    [Fact]
    public void AThrowingAdmissionClosureCannotSkipLaterOwnerClosuresOrSafetyCleanup()
    {
        var steps = new ShutdownSteps(CancellationToken.None, static () => DateTimeOffset.MaxValue);
        var failedClosure = new InvalidOperationException("device admission failed");
        List<string> completed = [];

        steps.Step("device admission", () => throw failedClosure);
        steps.Step("common admission", () => completed.Add("common admission"));
        steps.Step("graphics admission", () => completed.Add("graphics admission"));
        steps.Step("controller safety", () => completed.Add("controller safety"));

        Assert.Equal(["common admission", "graphics admission", "controller safety"], completed);
        Assert.Same(failedClosure, Assert.Single(steps.Failures));
    }

    [Fact]
    public async Task TightenedDeadlineReleasesTheJoinAndRetainsUnfinishedWork()
    {
        using var deadline = new CancellationTokenSource();
        var owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joining = ShutdownSteps.JoinWithinDeadlineAsync(owner.Task, deadline.Token, "device power transition");

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

        await ShutdownSteps.JoinWithinDeadlineAsync(Task.CompletedTask, deadline.Token, "controller safety");
    }

    [Fact]
    public async Task FaultedCleanupIsReportedEvenAfterTheDeadline()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        var failure = new InvalidOperationException("controller release failed");

        var reported = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShutdownSteps.JoinWithinDeadlineAsync(Task.FromException(failure), deadline.Token, "controller safety"));

        Assert.Same(failure, reported);
    }

    [Fact]
    public async Task OwnerCancellationRemainsDistinctFromDeadlineExpiry()
    {
        using var deadline = new CancellationTokenSource();
        using var ownerCancellation = new CancellationTokenSource();
        ownerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShutdownSteps.JoinWithinDeadlineAsync(
            Task.FromCanceled(ownerCancellation.Token), deadline.Token, "startup"));
    }
}
