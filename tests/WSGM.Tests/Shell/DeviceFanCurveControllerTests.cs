using System.Threading.Channels;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Shell;

namespace WSGM.Tests.Shell;

public sealed class DeviceFanCurveControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly CurvePoint[] Curve = [new(40, 20), new(80, 100)];

    [Theory]
    [InlineData(10, 20)]
    [InlineData(40, 20)]
    [InlineData(60, 60)]
    [InlineData(90, 100)]
    public void InterpolationUsesEndpointsAndLinearDuty(double temperature, int expected)
    {
        Assert.Equal(expected, DeviceFanCurveController.Interpolate(Curve, temperature));
    }

    [Fact]
    public void InterpolationRespectsEachFansWritableBounds()
    {
        Assert.Equal(30, DeviceFanCurveController.Interpolate(Curve, 10, 30, 90));
        Assert.Equal(90, DeviceFanCurveController.Interpolate(Curve, 90, 30, 90));
    }

    [Fact]
    public async Task LogicalFansUseTheirOwnTemperatureAndOnlyChangedDutiesAreWritten()
    {
        TickGate ticks = new();
        Dictionary<string, double> temperatures = new() { ["cpu"] = 50, ["gpu"] = 70 };
        List<(string Fan, int Duty)> writes = [];
        await using DeviceFanCurveController controller = new(
            fan => new DeviceFanCurveTemperature(temperatures[fan], Now),
            (fan, duty, _) =>
            {
                writes.Add((fan, duty));
                return ValueTask.FromResult(new DeviceFanCurveWriteResult(true));
            }, _ => { }, now: () => Now, waitTick: ticks.WaitAsync);
        await controller.ConfigureAsync(Curve,
            [new DeviceFanCurveFan("cpu"), new DeviceFanCurveFan("gpu", MaximumDuty: 75)], true);
        await ticks.WaitForLoopAsync();
        Assert.Equal([("cpu", 40), ("gpu", 75)], writes);

        await ticks.AdvanceAsync();
        Assert.Equal(2, writes.Count);
        temperatures["cpu"] = 60;
        await ticks.AdvanceAsync();
        Assert.Equal(("cpu", 60), writes[^1]);
        Assert.Equal(3, writes.Count);

        await controller.ConfigureAsync([new CurvePoint(40, 60), new CurvePoint(80, 100)],
            [new DeviceFanCurveFan("cpu"), new DeviceFanCurveFan("gpu", MaximumDuty: 75)], true);
        await ticks.WaitForLoopAsync();
        Assert.Equal(("cpu", 80), writes[^1]);
        Assert.Equal(4, writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrStaleTemperatureReleasesAutomaticOnceAndNeverWritesZero(bool stale)
    {
        TickGate ticks = new();
        DeviceFanCurveTemperature sample = new(stale ? 60 : null, Now - TimeSpan.FromSeconds(4));
        List<int> writes = [];
        List<string?> statuses = [];
        var releases = 0;
        await using DeviceFanCurveController controller = new(
            _ => sample,
            (_, duty, _) =>
            {
                writes.Add(duty);
                return ValueTask.FromResult(new DeviceFanCurveWriteResult(true));
            }, statuses.Add,
            (_, _) =>
            {
                releases++;
                return ValueTask.CompletedTask;
            }, () => Now, ticks.WaitAsync);
        await controller.ConfigureAsync(Curve, [new DeviceFanCurveFan("cpu")], true);
        await ticks.WaitForLoopAsync();
        await ticks.AdvanceAsync();
        Assert.Empty(writes);
        Assert.Equal(1, releases);
        Assert.Contains("fresh temperature", Assert.Single(statuses));

        sample = new DeviceFanCurveTemperature(60, Now);
        await ticks.AdvanceAsync();
        Assert.Equal([60], writes);
        Assert.Null(statuses[^1]);
    }

    [Fact]
    public async Task UncertainWriteDoesNotRetryOnTicksProfileReloadOrResume()
    {
        var attempts = 0;
        List<string?> statuses = [];
        TickGate ticks = new();
        await using DeviceFanCurveController controller = new(
            _ => new DeviceFanCurveTemperature(60, Now),
            (_, _, _) =>
            {
                attempts++;
                return ValueTask.FromResult(new DeviceFanCurveWriteResult(false,
                    "The fan write outcome is uncertain."));
            }, statuses.Add, now: () => Now, waitTick: ticks.WaitAsync);
        await controller.ConfigureAsync(Curve, [new DeviceFanCurveFan("cpu")], true);
        await controller.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.ConfigureAsync([new CurvePoint(40, 30), new CurvePoint(80, 100)],
            [new DeviceFanCurveFan("cpu")], true);
        await controller.SuspendAsync();
        await controller.ResumeAsync();
        Assert.Equal(1, attempts);
        Assert.Contains("uncertain", Assert.Single(statuses));

        await controller.ConfigureAsync(Curve, [new DeviceFanCurveFan("cpu")], true, true);
        await controller.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, attempts);
        await controller.ConfigureAsync(Curve, [new DeviceFanCurveFan("cpu")], false);
        await controller.ConfigureAsync(Curve, [new DeviceFanCurveFan("cpu")], true);
        await controller.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task SuspendStopAndDisposeJoinTheOwnedLoopAndReleaseEveryFan()
    {
        TickGate ticks = new();
        List<string> released = [];
        var attempts = 0;
        DeviceFanCurveController controller = new(
            _ => new DeviceFanCurveTemperature(60, Now),
            (_, _, _) =>
            {
                attempts++;
                return ValueTask.FromResult(new DeviceFanCurveWriteResult(true));
            }, _ => { },
            (fan, _) =>
            {
                released.Add(fan);
                return ValueTask.CompletedTask;
            }, () => Now, ticks.WaitAsync);
        await controller.ConfigureAsync(Curve, [new DeviceFanCurveFan("cpu"), new DeviceFanCurveFan("gpu")], true);
        await ticks.WaitForLoopAsync();
        await controller.SuspendAsync();
        Assert.True(controller.Completion.IsCompleted);
        Assert.Equal(["cpu", "gpu"], released);
        await controller.ResumeAsync();
        await ticks.WaitForLoopAsync();
        Assert.Equal(4, attempts);
        await controller.StopAsync();
        Assert.True(controller.Completion.IsCompleted);
        Assert.Equal(["cpu", "gpu", "cpu", "gpu"], released);
        await controller.ResumeAsync();
        Assert.Equal(4, attempts);
        await controller.DisposeAsync();
        await controller.DisposeAsync();
        Assert.True(controller.Completion.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            controller.ConfigureAsync(Curve, [new DeviceFanCurveFan("cpu")], true));
    }

    [Fact]
    public async Task StopWaitsForAnInFlightDutyWriteToFinishBeforeReleasingAutomaticMode()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = false;
        await using DeviceFanCurveController controller = new(
            _ => new DeviceFanCurveTemperature(60, Now),
            async (_, _, _) =>
            {
                entered.TrySetResult();
                await finish.Task;
                return new DeviceFanCurveWriteResult(true);
            }, _ => { },
            (_, _) =>
            {
                released = true;
                return ValueTask.CompletedTask;
            }, () => Now);
        await controller.ConfigureAsync(Curve, [new DeviceFanCurveFan("cpu")], true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopping = controller.StopAsync();
        Assert.False(stopping.IsCompleted);
        Assert.False(released);
        finish.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(released);
        Assert.True(controller.Completion.IsCompleted);
    }

    private sealed class TickGate
    {
        private readonly Channel<bool> _ticks = Channel.CreateUnbounded<bool>();
        private readonly Channel<bool> _waiting = Channel.CreateUnbounded<bool>();

        internal async ValueTask WaitAsync(CancellationToken token)
        {
            _waiting.Writer.TryWrite(true);
            await _ticks.Reader.ReadAsync(token);
        }

        internal Task WaitForLoopAsync()
        {
            return _waiting.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }

        internal async Task AdvanceAsync()
        {
            _ticks.Writer.TryWrite(true);
            await WaitForLoopAsync();
        }
    }
}
