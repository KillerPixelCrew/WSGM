using System.Numerics;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

public sealed class WindowsMotionSourceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PhysicalImuValuesUseTheSteamDeckApplicationAxisBasisOnce()
    {
        var sample = WindowsClawMotionSource.CreateSample(
            new Vector3(1f, 2f, 3f),
            Timestamp,
            new Vector3(0.25f, 0.75f, -0.5f));

        Assert.True(sample.HasAccelerometer);
        Assert.Equal(0.25f, sample.AccelX);
        Assert.Equal(-0.5f, sample.AccelY);
        Assert.Equal(-0.75f, sample.AccelZ);
        Assert.Equal(1f, sample.GyroX);
        Assert.Equal(3f, sample.GyroY);
        Assert.Equal(-2f, sample.GyroZ);
        Assert.Equal(Timestamp, sample.SensorTimestamp);
    }

    [Fact]
    public void MissingAccelerometerDataIsNotApproximated()
    {
        var sample = WindowsClawMotionSource.CreateSample(new Vector3(1f, 2f, 3f), Timestamp, null);

        Assert.False(sample.HasAccelerometer);
        Assert.Equal(0f, sample.AccelX);
        Assert.Equal(0f, sample.AccelY);
        Assert.Equal(0f, sample.AccelZ);
    }

    [Fact]
    public void SubDegreePhysicalGyroCrossesTheAxisTransformContinuously()
    {
        var sample = WindowsClawMotionSource.CreateSample(
            new Vector3(0.07f, -0.14f, 0.21f),
            Timestamp,
            Vector3.UnitZ);

        Assert.Equal(0.07f, sample.GyroX);
        Assert.Equal(0.21f, sample.GyroY);
        Assert.Equal(0.14f, sample.GyroZ);
    }

    [Theory]
    [InlineData("Physical Accelerometer", "Physical Accelerometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_8087&PID_0AC2", true)]
    [InlineData("Physical Gyrometer", "Physical Gyrometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_8087&PID_0AC2", true)]
    [InlineData("Calibrated Accelerometer", "Physical Accelerometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_8087&PID_0AC2", false)]
    [InlineData("Physical Gyrometer", "Physical Accelerometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_8087&PID_0AC2", false)]
    [InlineData("Physical Accelerometer", "Physical Accelerometer", "c2fb0f5f-e2d2-4c78-bcd0-352a9582819d", "HID#VID_8087&PID_0AC2", false)]
    [InlineData("Physical Accelerometer", "Physical Accelerometer", "e83af229-8640-4d18-a213-e22675ebb2c3", "HID#VID_1234&PID_5678", false)]
    public void OnlyTheReviewedCustomIntelCollectionMatches(
        string name,
        string expectedName,
        string type,
        string path,
        bool expected)
    {
        Assert.Equal(
            expected,
            LegacyPhysicalMotionSensors.MatchesExpectedIdentity(
                name,
                Guid.Parse(type),
                path,
                expectedName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutRetainsSensorsAndBlocksRestartUntilBothWorkersFinish(bool stuckProducer)
    {
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SensorOwner sensors = new();
        CancellationTokenSource cancellation = new();
        var opens = 0;
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
