using System.Runtime.InteropServices;
using WSGM.Core;
using WSGM.Input;
using HapticOutputFrame = LibHandheld.Contracts.HapticOutputFrame;

namespace WSGM.Tests.Input;

public sealed class ControllerDependencyAdapterTests
{
    [Fact]
    public void ProductionBackendAdvertisesExactlyTheTargetsWithWireEncoders()
    {
        Assert.Equal(
            [
                ManagedControllerTarget.SteamDeckComposite,
                ManagedControllerTarget.Xbox360,
                ManagedControllerTarget.DualShock4
            ],
            ViiperControllerBackend.SupportedTargets);
    }

    [Theory]
    [InlineData(ManagedControllerTarget.Xbox360, new byte[] { 128, 255 }, 128 / 255f, 1f)]
    [InlineData(
        ManagedControllerTarget.DualShock4,
        new byte[] { 0x07, 64, 192, 0, 0, 0, 0, 0 },
        192 / 255f,
        64 / 255f)]
    public void TargetFeedbackUsesTheCorrectMotorOrder(
        ManagedControllerTarget target,
        byte[] report,
        float expectedLow,
        float expectedHigh)
    {
        var feedback = Assert.IsType<DecodedHapticFeedback>(
            ViiperControllerBackend.DecodeFeedback(target, report));

        Assert.Equal(expectedLow, feedback.LowFrequency, 5);
        Assert.Equal(expectedHigh, feedback.HighFrequency, 5);
        Assert.Null(feedback.StopAfter);
    }

    [Fact]
    public void DualShock4FeedbackWithoutTheRumbleFlagProducesNoOutput()
    {
        // An LED-only output report carries zero motor bytes that must not stop the motors.
        byte[] report = [0x02, 0, 0, 255, 0, 0, 0, 0];

        Assert.Null(ViiperControllerBackend.DecodeFeedback(
            ManagedControllerTarget.DualShock4,
            report));
    }

    [Fact]
    public void SteamDeckFeedbackKeepsItsSixteenBitMotorScale()
    {
        // Live gameplay delivers rumble values past 0x8000, so the scale is the full unsigned
        // range — a signed divisor clamps the upper half of the envelope and crushes dynamics.
        byte[] report = [0xEB, 0, 0, 0, 0, 0, 0x80, 0xFF, 0xFF];

        var feedback = Assert.IsType<DecodedHapticFeedback>(
            ViiperControllerBackend.DecodeFeedback(
                ManagedControllerTarget.SteamDeckComposite,
                report));

        Assert.Equal(32768 / (float)ushort.MaxValue, feedback.LowFrequency, 5);
        Assert.Equal(1f, feedback.HighFrequency);
        Assert.Null(feedback.StopAfter);
    }

    [Fact]
    public void SteamDeckHapticEventBecomesABoundedPulse()
    {
        // Steam-private 0xDC event as captured live: side 1, command 2 (strong click).
        byte[] report = [0xDC, 0x02, 0x01, 0x02];

        var feedback = Assert.IsType<DecodedHapticFeedback>(
            ViiperControllerBackend.DecodeFeedback(
                ManagedControllerTarget.SteamDeckComposite,
                report));

        Assert.Equal(1f, feedback.LowFrequency);
        Assert.Equal(1f, feedback.HighFrequency);
        Assert.NotNull(feedback.StopAfter);
    }

    [Fact]
    public void SteamDeckHapticGainSetProducesNoOutput()
    {
        byte[] report = [0xE2, 0x02, 0x01, 0x20];

        Assert.Null(ViiperControllerBackend.DecodeFeedback(
            ManagedControllerTarget.SteamDeckComposite,
            report));
    }

    [Theory]
    // The live-captured layout carries a small enum level, not a byte intensity: presses arrive
    // as level 3, releases as level 2. The decode is protocol intent — level over the enum
    // range — and motor-specific rendering happens in the output router, not here.
    [InlineData(0, 0, 0f)]
    [InlineData(1, 0, 1f / 3f)]
    [InlineData(2, 0, 2f / 3f)]
    [InlineData(3, 3, 1f)]
    public void SteamDeckClickHapticBecomesABoundedErmTick(
        byte intensity,
        sbyte gain,
        float expected)
    {
        byte[] report = [0xEA, 0x0D, 0, 2, intensity, unchecked((byte)gain)];

        var feedback = Assert.IsType<DecodedHapticFeedback>(
            ViiperControllerBackend.DecodeFeedback(
                ManagedControllerTarget.SteamDeckComposite,
                report));

        Assert.Equal(expected, feedback.LowFrequency, 5);
        Assert.Equal(expected, feedback.HighFrequency, 5);
        if (expected > 0f)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(35), feedback.StopAfter);
        }
    }

    [Fact]
    public void SteamDeckHapticPulseCarriesBoundedMotorStopTime()
    {
        byte[] report = [0x8F, 0, 0, 0, 0, 0xE8, 0x03, 2, 0, 3];

        var feedback = Assert.IsType<DecodedHapticFeedback>(
            ViiperControllerBackend.DecodeFeedback(
                ManagedControllerTarget.SteamDeckComposite,
                report));

        Assert.Equal(35 / 255f, feedback.LowFrequency, 5);
        Assert.Equal(feedback.LowFrequency, feedback.HighFrequency);
        Assert.Equal(TimeSpan.FromMilliseconds(2), feedback.StopAfter);
    }

    [Fact]
    public void MotorFloorCompressesEventChannelsAndKeepsZeroSilent()
    {
        HapticOutputFrame frame = new()
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            LowFrequency = 0.008f,
            HighFrequency = 0f
        };

        var floored = ControllerOutputRouter.FloorForMotors(frame, 0.35f);

        Assert.Equal(0.35f + 0.65f * 0.008f, floored.LowFrequency, 5);
        Assert.Equal(0f, floored.HighFrequency);
        Assert.Equal(frame, ControllerOutputRouter.FloorForMotors(frame, 0f));
    }

    [Fact]
    public void SafeNativeRunsTheCallExactlyOnce()
    {
        // Regression: a self-forwarding overload once made this call recurse until the stack was
        // exhausted, which killed every live target replacement and shutdown.
        var calls = 0;

        ViiperControllerBackend.SafeNative(() => ++calls, "count");

        Assert.Equal(1, calls);
    }

    [Fact]
    public void SafeNativeSwallowsOnlyNativeBindingFailures()
    {
        ViiperControllerBackend.SafeNative(
            () => throw new SEHException(),
            "fail natively");

        Assert.Throws<InvalidOperationException>(() => ViiperControllerBackend.SafeNative(
            () => throw new InvalidOperationException("managed"),
            "fail in managed code"));
    }
}
