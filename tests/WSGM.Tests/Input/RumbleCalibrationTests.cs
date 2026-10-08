using WSGM.Core;
using WSGM.Device.Sdk.Input;
using WSGM.Input;

namespace WSGM.Tests.Input;

public sealed class RumbleCalibrationTests
{
    private static readonly HapticCapabilities Motors = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        MinimumStartIntensity = 0.2f
    };

    [Fact]
    public void ContinuousQuietRumbleIsScaledWithoutAPerceptualFloor()
    {
        var frame = new HapticOutputFrame { Timestamp = DateTimeOffset.UnixEpoch, LowFrequency = 0.01f };
        var settings = new RumbleCalibrationConfig { StrengthPercent = 50, MinimumStrengthPercent = 80 };
        var calibrated = ControllerOutputRouter.Calibrate(frame, Motors, settings, false);
        Assert.Equal(0.005f, calibrated.LowFrequency, 6);
        Assert.Equal(0f, calibrated.HighFrequency);
    }

    [Fact]
    public void ZeroGainAndExplicitStopsStaySilentEvenWithAMaximumFloor()
    {
        var settings = new RumbleCalibrationConfig { StrengthPercent = 0, MinimumStrengthPercent = 100 };
        var frame = new HapticOutputFrame { Timestamp = DateTimeOffset.UnixEpoch, LowFrequency = 1f };
        Assert.True(ControllerOutputRouter.Calibrate(frame, Motors, settings, true).IsSilent);
        settings.StrengthPercent = 100;
        Assert.True(ControllerOutputRouter.Calibrate(HapticOutputFrame.Stop(DateTimeOffset.UnixEpoch),
            Motors, settings, true).IsSilent);
    }

    [Fact]
    public void BoundedEffectsHonorTheLargerFloorAndNeverInventUnsupportedChannels()
    {
        var frame = new HapticOutputFrame
        {
            Timestamp = DateTimeOffset.UnixEpoch, LowFrequency = 0.01f, LeftTrigger = 1f
        };
        var calibrated = ControllerOutputRouter.Calibrate(frame, Motors,
            new RumbleCalibrationConfig { MinimumStrengthPercent = 40 }, true);
        Assert.Equal(0.406f, calibrated.LowFrequency, 5);
        Assert.Equal(0f, calibrated.LeftTrigger);
    }

    [Fact]
    public async Task APreviewCannotBeExtendedByRepeatedRequestsAndStopZerosTheMotors()
    {
        var backend = new DeterministicFakeControllerBackend();
        var sink = new DeterministicFakeHapticSink(Motors);
        await using var router = new ControllerOutputRouter(backend, sink);
        router.Attach(new ControllerTargetHandle(ManagedControllerTarget.Xbox360, 1));
        router.ApplyCalibration(new RumbleCalibrationConfig
            { MinimumStrengthPercent = 40, MinimumPulseMilliseconds = 500 });
        Assert.True(await router.PreviewAsync(true, CancellationToken.None));
        Assert.Equal(0.4f, sink.Frames[0].LowFrequency);
        Assert.False(await router.PreviewAsync(true, CancellationToken.None));
        await router.StopPreviewAsync();
        Assert.True(sink.Frames[^1].IsSilent);
        router.Detach(1);
        Assert.False(await router.PreviewAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task OwnerDisposalRetractsAnActivePreview()
    {
        var sink = new DeterministicFakeHapticSink(Motors);
        var router = new ControllerOutputRouter(new DeterministicFakeControllerBackend(), sink);
        router.Attach(new ControllerTargetHandle(ManagedControllerTarget.Xbox360, 1));
        router.ApplyCalibration(new RumbleCalibrationConfig { MinimumPulseMilliseconds = 500 });
        Assert.True(await router.PreviewAsync(false, CancellationToken.None));
        await router.DisposeAsync();
        Assert.True(sink.Frames[^1].IsSilent);
    }

    [Fact]
    public async Task PreviewEndsWithoutAnotherFeedbackPacket()
    {
        var backend = new DeterministicFakeControllerBackend();
        var sink = new DeterministicFakeHapticSink(Motors);
        await using var router = new ControllerOutputRouter(backend, sink);
        router.Attach(new ControllerTargetHandle(ManagedControllerTarget.Xbox360, 1));
        router.ApplyCalibration(new RumbleCalibrationConfig { MinimumPulseMilliseconds = 25 });
        Assert.True(await router.PreviewAsync(false, CancellationToken.None));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!sink.Frames[^1].IsSilent && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(sink.Frames[^1].IsSilent);
    }
}
