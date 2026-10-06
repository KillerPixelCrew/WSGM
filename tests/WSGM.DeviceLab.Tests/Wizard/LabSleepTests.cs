using System.Text.Json;
using WSGM.DeviceLab.Wizard;
using WSGM.Testing;

namespace WSGM.DeviceLab.Tests.Wizard;

public sealed class LabSleepTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Run_WaitsForRestoreBeforeSavingCompletion()
    {
        using TemporaryDirectory temporary = new();
        var project = Project(temporary);
        var attempt = project.BeginAttempt(LabStages.Sleep, Now);
        TaskCompletionSource<string?> restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = LabSleep.RunAsync(project, attempt,
            () => Task.FromResult((LabSegmentStatus.Completed, "Buttons work.")), () => restored.Task);

        Assert.False(operation.IsCompleted);
        Assert.Equal(LabSegmentStatus.NotStarted, LabProject.Open(project.Directory).Segment(LabStages.Sleep).Status);
        restored.SetResult(null);
        Assert.Null(await operation);

        Assert.Equal(LabSegmentStatus.Completed, LabProject.Open(project.Directory).Segment(LabStages.Sleep).Status);
        using var evidence = RestoreEvidence(attempt);
        Assert.True(evidence.RootElement.GetProperty("restored").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_FailedRestoreOverridesCompletedOrSkippedAndKeepsEvidence(bool skipped)
    {
        using TemporaryDirectory temporary = new();
        var project = Project(temporary);
        var attempt = project.BeginAttempt(LabStages.Sleep, Now);
        var summary = skipped ? "Skipped by the tester." : "Buttons work.";
        project.WriteEvidence(attempt, "sleep", new { Outcome = summary });
        var sleepBytes = File.ReadAllBytes(Path.Combine(attempt, "sleep.json"));

        var problem = await LabSleep.RunAsync(project, attempt,
            () => Task.FromResult((skipped ? LabSegmentStatus.Skipped : LabSegmentStatus.Completed, summary)),
            () => Task.FromResult<string?>("Original controller mode did not return."));

        var state = LabProject.Open(project.Directory).Segment(LabStages.Sleep);
        Assert.Equal(LabSegmentStatus.Failed, state.Status);
        Assert.Contains(summary, state.Summary);
        Assert.Contains(problem!, state.Summary);
        Assert.Equal(sleepBytes, File.ReadAllBytes(Path.Combine(attempt, "sleep.json")));
        using var evidence = RestoreEvidence(attempt);
        Assert.False(evidence.RootElement.GetProperty("restored").GetBoolean());
        Assert.Equal(problem, evidence.RootElement.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Run_SkippedCycleWithSuccessfulRestoreStaysSkipped()
    {
        using TemporaryDirectory temporary = new();
        var project = Project(temporary);
        var attempt = project.BeginAttempt(LabStages.Sleep, Now);

        await LabSleep.RunAsync(project, attempt,
            () => Task.FromResult((LabSegmentStatus.Skipped, "Skipped by the tester.")),
            () => Task.FromResult<string?>(null));

        Assert.Equal(LabSegmentStatus.Skipped, LabProject.Open(project.Directory).Segment(LabStages.Sleep).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_CancelledInitStillRestoresAndRecordsAnyRestoreFailure(bool restoreFailed)
    {
        using TemporaryDirectory temporary = new();
        var project = Project(temporary);
        var attempt = project.BeginAttempt(LabStages.Sleep, Now);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        var restores = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LabSleep.RunAsync(project, attempt,
            () => Task.FromCanceled<(LabSegmentStatus Status, string Summary)>(cancellation.Token),
            () =>
            {
                restores++;
                return Task.FromResult(restoreFailed ? "Controller is still in test mode." : null);
            }));

        Assert.Equal(1, restores);
        var state = LabProject.Open(project.Directory).Segment(LabStages.Sleep);
        Assert.Equal(restoreFailed ? LabSegmentStatus.Failed : LabSegmentStatus.NotStarted, state.Status);
        using var evidence = RestoreEvidence(attempt);
        Assert.Equal(!restoreFailed, evidence.RootElement.GetProperty("restored").GetBoolean());
    }

    [Fact]
    public async Task Run_ThrowingRestoreRecordsFailureWithoutRepeatingIt()
    {
        using TemporaryDirectory temporary = new();
        var project = Project(temporary);
        var attempt = project.BeginAttempt(LabStages.Sleep, Now);
        var restores = 0;

        var problem = await LabSleep.RunAsync(project, attempt,
            () => Task.FromResult((LabSegmentStatus.Completed, "Buttons work.")),
            () =>
            {
                restores++;
                throw new IOException("The worker connection was lost.");
            });

        Assert.Equal(1, restores);
        Assert.Equal("The worker connection was lost.", problem);
        Assert.Equal(LabSegmentStatus.Failed, LabProject.Open(project.Directory).Segment(LabStages.Sleep).Status);
        using var evidence = RestoreEvidence(attempt);
        Assert.Equal(problem, evidence.RootElement.GetProperty("problem").GetString());
    }

    private static LabProject Project(TemporaryDirectory temporary)
    {
        return LabProject.Create(Path.Combine(temporary.Root, "project"), LabStages.Ids, "1.0.0", Now);
    }

    private static JsonDocument RestoreEvidence(string attempt)
    {
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt, "controller-restore.json")));
    }
}
