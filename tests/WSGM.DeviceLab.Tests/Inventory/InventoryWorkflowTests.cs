using WSGM.DeviceLab.Inventory;

namespace WSGM.Device.Tests;

public sealed class InventoryWorkflowTests
{
    [Fact]
    public async Task CancellationReturnsBeforeAnUncooperativeSynchronousProviderFinishes()
    {
        var worker = new CancellableSynchronousWorker();
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        using CancellationTokenSource cancellation = new();
        Task<int> call = Task.Run(() => worker.Run(
            _ =>
            {
                started.Set();
                release.Wait();
                return 1;
            },
            cancellation.Token));

        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await call.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
        }

        int next = await Task.Run(() => worker.Run(_ => 2, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, next);
    }

    [Fact]
    public void WhitespaceAfterEnvironmentExpansionHasNoExecutablePath()
    {
        const string variable = "WSGM_TEST_EMPTY_COMMAND";
        string? previous = Environment.GetEnvironmentVariable(variable);
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
        string path = directory.GetPath("inventory.tmp");
        using (FileStream locked = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            DeviceLabInventoryResult result = Assert.IsType<DeviceLabInventoryResult>(
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
