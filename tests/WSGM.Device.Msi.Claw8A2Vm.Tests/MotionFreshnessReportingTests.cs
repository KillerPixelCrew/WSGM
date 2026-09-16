using WSGM.Device.Msi.Claw8A2Vm.Tests.Fakes;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Plugin;
using WSGM.Device.Sdk.Testing;

namespace WSGM.Device.Msi.Claw8A2Vm.Tests;

[Collection("plugin-trace")]
public sealed class MotionFreshnessReportingTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 18, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        PluginTrace.Install(null);
    }

    [Fact]
    public async Task CrossingTheFreshnessCapIsNotReported()
    {
        // Measured Intel transport jitter clusters just past the cap. Reporting each crossing put
        // two alternating lines into the log about 1.3 times a second.
        var (motion, host) = await StartAsync();

        motion.Current(Start + MotionService.MaximumMotionAge + TimeSpan.FromMilliseconds(9));

        Assert.Empty(host.Changes);
        Assert.Empty(host.Traces);
    }

    [Fact]
    public async Task APauseThatOutlastsJitterIsReportedOnce()
    {
        var (motion, host) = await StartAsync();

        var past = Start + MotionService.StaleReportDelay + TimeSpan.FromMilliseconds(1);
        for (var frame = 0; frame < 200; frame++)
        {
            motion.Current(past + TimeSpan.FromMilliseconds(frame * 8));
        }

        var line =
            Assert.Single(host.Changes);
        Assert.Equal("motion", line.Scope);
        Assert.Equal("freshness", line.Key);
        Assert.Contains("holding rest", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResumeIsReportedOnlyWhereThePauseWas()
    {
        var (motion, host, publish) =
            await StartCapturingAsync();

        // A crossing too brief to mention stays unmentioned at both ends.
        motion.Current(Start + MotionService.MaximumMotionAge + TimeSpan.FromMilliseconds(9));
        await publish(Sample(Start + TimeSpan.FromSeconds(1)));
        Assert.Empty(host.Changes);

        // A reported pause owes a resume.
        motion.Current(Start + TimeSpan.FromSeconds(1) + MotionService.StaleReportDelay
                       + TimeSpan.FromMilliseconds(1));
        await publish(Sample(Start + TimeSpan.FromSeconds(3)));

        Assert.Equal(2, host.Changes.Count);
        Assert.Contains("holding rest", host.Changes[0].Message, StringComparison.Ordinal);
        Assert.Contains("resumed", host.Changes[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFreshReadingRearmsTheReport()
    {
        var (motion, host, publish) =
            await StartCapturingAsync();

        for (var pause = 1; pause <= 3; pause++)
        {
            var reading = Start + TimeSpan.FromSeconds(pause * 10);
            await publish(Sample(reading));
            motion.Current(reading + MotionService.StaleReportDelay + TimeSpan.FromMilliseconds(1));
        }

        // Three pauses, each reported once, each ended by the reading that opened the next.
        Assert.Equal(5, host.Changes.Count);
    }

    private static MotionSample Sample(DateTimeOffset stamp)
    {
        return new MotionSample
        {
            HasGyro = true,
            HasAccelerometer = true,
            AccelZ = 1f,
            SensorTimestamp = stamp
        };
    }

    private static async Task<(MotionService Motion, TestPluginHostAdapter Host)> StartAsync()
    {
        var (motion, host, publish) =
            await StartCapturingAsync();
        await publish(Sample(Start));
        return (motion, host);
    }

    private static async Task<(
        MotionService Motion,
        TestPluginHostAdapter Host,
        Func<MotionSample, ValueTask> Publish)> StartCapturingAsync()
    {
        TestPluginHostAdapter host = new(1);
        PluginTrace.Install(host);
        FakeMotionSource source = new();
        MotionService motion = new(source);
        await motion.AcquireAsync(
            new ClawCycleContext(1, DateTimeOffset.MaxValue),
            CancellationToken.None);
        return (motion, host, source.Publish!);
    }
}
