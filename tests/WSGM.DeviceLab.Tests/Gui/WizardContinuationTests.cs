using WSGM.DeviceLab.Gui;

namespace WSGM.DeviceLab.Tests.Gui;

public sealed class WizardContinuationTests
{
    [Fact]
    public async Task ContinueKeepsTheNextStageTrackedAndCloseWaitsForItsFinally()
    {
        var operation = Task.CompletedTask;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        Assert.True(WizardWindow.ContinueCompletedOperation(operation, () => operation = NextStage()));
        Assert.False(operation.IsCompleted);
        var close = Close();
        Assert.False(close.IsCompleted);
        release.SetResult();
        await close;
        Assert.True(cleaned);
        return;

        async Task NextStage()
        {
            try
            {
                await release.Task;
            }
            finally
            {
                cleaned = true;
            }
        }

        async Task Close()
        {
            await operation;
            Assert.True(cleaned);
        }
    }

    [Fact]
    public void ContinueCannotStartAnotherStageBesideARestorationAttempt()
    {
        TaskCompletionSource restoring = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var advanced = false;

        Assert.False(WizardWindow.ContinueCompletedOperation(restoring.Task, () => advanced = true));
        Assert.False(advanced);
        restoring.SetResult();
        Assert.True(WizardWindow.ContinueCompletedOperation(restoring.Task, () => advanced = true));
        Assert.True(advanced);
    }
}
