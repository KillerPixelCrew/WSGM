using WSGM.Device.Contracts.Input;

namespace WSGM.Device.Contracts.Tests;

/// <summary>
/// The executable specification of output routing and target consumption: what reaches the physical
/// device, and what a target is allowed to do with input it cannot represent.
/// </summary>
public class OutputRoutingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldDeliver_AFrameFromTheCurrentTargetWithNoZeroTrigger_IsDelivered()
    {
        Assert.True(OutputRouting.ShouldDeliver(Frame(5), 5, ZeroOutputTrigger.None));
    }

    [Fact]
    public void ShouldDeliver_AFrameFromASupersededTarget_IsDropped()
    {
        // It was computed for a controller that no longer exists. Delivering it would drive the
        // current one with another target's effect.
        Assert.False(OutputRouting.ShouldDeliver(Frame(4), 5, ZeroOutputTrigger.None));
    }

    [Theory]
    [InlineData(ZeroOutputTrigger.UiCaptureClaimed)]
    [InlineData(ZeroOutputTrigger.GameExited)]
    [InlineData(ZeroOutputTrigger.Suspending)]
    [InlineData(ZeroOutputTrigger.PhysicalDisconnected)]
    [InlineData(ZeroOutputTrigger.PluginDisabled)]
    [InlineData(ZeroOutputTrigger.SourceFaulted)]
    [InlineData(ZeroOutputTrigger.TargetRemoved)]
    [InlineData(ZeroOutputTrigger.SourceSwitching)]
    public void ShouldDeliver_IsRefusedWhileAnyZeroTriggerIsActive(ZeroOutputTrigger trigger)
    {
        Assert.False(OutputRouting.ShouldDeliver(Frame(5), 5, trigger));
    }

    [Fact]
    public void RequiresStop_WhenOutputIsRunningAndATriggerFires()
    {
        // Simply ceasing to send frames leaves the last one latched and the motor running.
        Assert.True(OutputRouting.RequiresStop(ZeroOutputTrigger.GameExited, outputActive: true));
    }

    [Fact]
    public void RequiresStop_IsUnnecessaryWhenNothingIsRunning()
    {
        Assert.False(OutputRouting.RequiresStop(ZeroOutputTrigger.GameExited, outputActive: false));
        Assert.False(OutputRouting.RequiresStop(ZeroOutputTrigger.None, outputActive: true));
    }

    [Fact]
    public void Stop_ProducesASilentFrame()
    {
        HapticOutputFrame stop = HapticOutputFrame.Stop(5, Now);

        Assert.True(stop.IsSilent);
        Assert.Equal(5, stop.TargetGeneration);
    }

    [Fact]
    public void Clamp_DropsUnsupportedChannelsWithoutRedistributingThem()
    {
        // Folding an unsupported trigger haptic into the rumble motors would invent an effect the
        // game never asked for - the output-side equivalent of gyro-to-stick conversion.
        HapticCapabilities twoMotorsOnly = new()
        {
            LowFrequency = OutputChannelSupport.Native,
            HighFrequency = OutputChannelSupport.Native,
        };

        HapticOutputFrame clamped = twoMotorsOnly.Clamp(Frame(5) with
        {
            LowFrequency = 0.5f,
            HighFrequency = 0.25f,
            LeftTrigger = 1.0f,
            RightTrigger = 1.0f,
        });

        Assert.Equal(0.5f, clamped.LowFrequency);
        Assert.Equal(0.25f, clamped.HighFrequency);
        Assert.Equal(0f, clamped.LeftTrigger);
        Assert.Equal(0f, clamped.RightTrigger);
    }

    [Fact]
    public void Consume_XboxTargetDropsRearPaddlesRatherThanRemappingThem()
    {
        // Forwarding a paddle as a face button would silently make it press a different control.
        CanonicalButtons consumed = VirtualTargetProfile.Xbox360.Consume(
            CanonicalButtons.A | CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2);

        Assert.Equal(CanonicalButtons.A, consumed);
    }

    [Fact]
    public void Consume_SteamDeckTargetKeepsRearPaddlesAndStickTouch()
    {
        CanonicalButtons input = CanonicalButtons.A | CanonicalButtons.RearPaddle1
            | CanonicalButtons.LeftStickTouch;

        Assert.Equal(input, VirtualTargetProfile.SteamDeck.Consume(input));
    }

    [Fact]
    public void Consume_DualShock4DropsStickTouchItDoesNotHave()
    {
        CanonicalButtons consumed = VirtualTargetProfile.DualShock4.Consume(
            CanonicalButtons.A | CanonicalButtons.LeftStickTouch);

        Assert.Equal(CanonicalButtons.A, consumed);
    }

    [Fact]
    public void OnlyTargetsWithNativeMotionAdvertiseIt()
    {
        // Gyro is passed through where the target supports motion and absent where it does not. It is
        // never converted into another input type.
        Assert.True(VirtualTargetProfile.SteamDeck.SupportsMotion);
        Assert.True(VirtualTargetProfile.DualShock4.SupportsMotion);
        Assert.False(VirtualTargetProfile.Xbox360.SupportsMotion);
    }

    private static HapticOutputFrame Frame(long targetGeneration) => new()
    {
        TargetGeneration = targetGeneration,
        LowFrequency = 0.5f,
        Timestamp = Now,
    };
}
