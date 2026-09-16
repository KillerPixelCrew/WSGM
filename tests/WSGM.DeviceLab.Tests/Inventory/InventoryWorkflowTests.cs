using WSGM.Device.Tests;
using WSGM.DeviceLab.Inventory;

namespace WSGM.DeviceLab.Tests.Inventory;

public sealed class InventoryWorkflowTests
{
    [Fact]
    public async Task CancellationReturnsBeforeAnUncooperativeSynchronousProviderFinishes()
    {
        var worker = new CancellableSynchronousWorker();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = new();
        var token = cancellation.Token;
        var call = Task.Run(() => worker.Run(
            _ =>
            {
                started.TrySetResult();
                release.Task.Wait(CancellationToken.None);
                return 1;
            },
            token), CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await call.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
        }

        var next = await Task.Run(() => worker.Run(_ => 2, CancellationToken.None), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(2, next);
    }

    [Fact]
    public void WhitespaceAfterEnvironmentExpansionHasNoExecutablePath()
    {
        const string variable = "WSGM_TEST_EMPTY_COMMAND";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "   ");
            Assert.Null(WindowsInventoryCollector.ExtractExecutablePath($"%{variable}%"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void CancelledInventoryReportsALockedTemporaryFileAndRemovesItOnceReleased()
    {
        using TemporaryDirectory directory = new();
        var path = directory.GetPath("inventory.tmp");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var result = Assert.IsType<DeviceLabInventoryResult>(
                DeviceLabInventoryWorkflow.CleanupCancelledWrite(path));
            Assert.Equal(DeviceLabInventoryStatus.WriteFailed, result.Status);
            Assert.Contains(path, result.Error);
            Assert.True(File.Exists(path));
        }
        Assert.Null(DeviceLabInventoryWorkflow.CleanupCancelledWrite(path));
        Assert.False(File.Exists(path));
        Assert.Null(DeviceLabInventoryWorkflow.CleanupCancelledWrite(path));
    }
}
