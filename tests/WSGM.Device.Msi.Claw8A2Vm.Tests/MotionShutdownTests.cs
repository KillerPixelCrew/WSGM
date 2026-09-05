namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class MotionShutdownTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutRetainsSensorsAndBlocksRestartUntilBothWorkersFinish(bool stuckProducer)
    {
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SensorOwner sensors = new();
        CancellationTokenSource cancellation = new();
        int opens = 0;
        WindowsClawMotionSource source = new(_ =>
        {
            opens++;
            return new MotionWorkerSession(sensors, cancellation,
                stuckProducer ? blocked.Task : Task.CompletedTask,
                stuckProducer ? Task.CompletedTask : blocked.Task);
        }, TimeSpan.FromMilliseconds(100));
        Assert.True(await source.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None));

        await Assert.ThrowsAsync<TimeoutException>(() => source.StopAsync(CancellationToken.None).AsTask());
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(0, sensors.Disposals);
        Assert.False(await source.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None));
        Assert.Equal(1, opens);

        blocked.SetResult();
        await source.StopAsync(CancellationToken.None);
        await source.DisposeAsync();
        await source.DisposeAsync();
        Assert.Equal(1, sensors.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => source.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task CancellationAndDisposeDoNotWaitForeverForAPublisher()
    {
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SensorOwner sensors = new();
        WindowsClawMotionSource source = new(
            _ => new MotionWorkerSession(sensors, new CancellationTokenSource(), Task.CompletedTask, blocked.Task),
            TimeSpan.FromMilliseconds(100));
        await source.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        using CancellationTokenSource caller = new();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.StopAsync(caller.Token).AsTask());
        await Assert.ThrowsAsync<TimeoutException>(() => source.DisposeAsync().AsTask());
        Assert.Equal(0, sensors.Disposals);

        blocked.SetResult();
        await source.DisposeAsync();
        Assert.Equal(1, sensors.Disposals);
    }

    [Fact]
    public async Task FailedProducerStillWaitsForPublisherBeforeDisposalAndReportsFailure()
    {
        TaskCompletionSource publisher = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SensorOwner sensors = new();
        WindowsClawMotionSource source = new(
            _ => new MotionWorkerSession(sensors, new CancellationTokenSource(),
                Task.FromException(new IOException("worker failed")), publisher.Task),
            TimeSpan.FromMilliseconds(100));
        await source.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(() => source.StopAsync(CancellationToken.None).AsTask());
        Assert.Equal(0, sensors.Disposals);
        publisher.SetResult();
        await Assert.ThrowsAsync<IOException>(() => source.StopAsync(CancellationToken.None).AsTask());
        Assert.Equal(1, sensors.Disposals);
        Assert.False(await source.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None));
    }

    private sealed class SensorOwner : IDisposable
    {
        public int Disposals { get; private set; }

        public void Dispose() => Disposals++;
    }
}
