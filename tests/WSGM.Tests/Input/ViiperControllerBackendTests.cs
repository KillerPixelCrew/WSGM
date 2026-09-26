using WSGM.Input;

namespace WSGM.Tests.Input;

/// <summary>The consumer-side motion request read out of the Steam Deck target's feedback frames.</summary>
public sealed class ViiperControllerBackendTests
{
    [Theory]
    [InlineData(new byte[] { 0x87, 0x03, 0x30, 0x1C, 0x00 }, true)] // IMU mode: raw accel and gyro
    [InlineData(new byte[] { 0x87, 0x03, 0x30, 0x00, 0x00 }, false)] // IMU mode off
    [InlineData(new byte[] { 0x00, 0x87, 0x03, 0x30, 0x01, 0x00 }, true)] // with the report id byte
    [InlineData(new byte[] { 0x87, 0x06, 0x09, 0x00, 0x00, 0x30, 0x02, 0x00 }, true)] // after lizard mode
    [InlineData(new byte[] { 0x86 }, false)] // factory reset
    [InlineData(new byte[] { 0x88 }, false)] // clear settings
    [InlineData(new byte[] { 0x8E }, false)] // load defaults
    public void TheImuModeSettingAndTheResetsSayWhetherMotionIsRequested(byte[] frame, bool expected)
    {
        Assert.True(ViiperControllerBackend.TryReadMotionRequest(frame, out var requested));
        Assert.Equal(expected, requested);
    }

    [Theory]
    [InlineData(new byte[] { 0x87, 0x03, 0x09, 0x01, 0x00 })] // lizard mode only
    [InlineData(new byte[] { 0x87, 0x02, 0x30, 0x01 })] // truncated triple
    [InlineData(new byte[] { 0xEB, 0x08, 0x00, 0x00, 0x00, 0x00 })] // rumble
    [InlineData(new byte[] { })]
    public void FramesThatDoNotTouchTheImuSayNothing(byte[] frame)
    {
        Assert.False(ViiperControllerBackend.TryReadMotionRequest(frame, out _));
    }
}
