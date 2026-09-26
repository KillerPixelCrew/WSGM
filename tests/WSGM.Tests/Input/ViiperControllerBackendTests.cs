using WSGM.Input;

namespace WSGM.Tests.Input;

/// <summary>The consumer-side motion signals read out of the Steam Deck target's feedback frames.</summary>
public sealed class ViiperControllerBackendTests
{
    // The signal is named rather than passed as the internal enum, which a public theory cannot take.
    [Theory]
    [InlineData(new byte[] { 0x87, 0x03, 0x30, 0x1C, 0x00 }, "ImuOn")] // IMU mode: raw accel and gyro
    [InlineData(new byte[] { 0x87, 0x03, 0x30, 0x00, 0x00 }, "ImuOff")] // IMU mode off
    [InlineData(new byte[] { 0x00, 0x87, 0x03, 0x30, 0x01, 0x00 }, "ImuOn")] // with the report id byte
    [InlineData(new byte[] { 0x87, 0x06, 0x09, 0x00, 0x00, 0x30, 0x02, 0x00 }, "ImuOn")] // after lizard mode
    [InlineData(new byte[] { 0x86 }, "ImuOff")] // factory reset
    [InlineData(new byte[] { 0x88 }, "ImuOff")] // clear settings
    [InlineData(new byte[] { 0x8E }, "ImuOff")] // load defaults
    // SDL's Deck driver feeding its lizard-mode watchdog: right trackpad mode to none, alone.
    [InlineData(new byte[] { 0x87, 0x03, 0x08, 0x07, 0x00 }, "ConsumerHeartbeat")]
    // SDL's Deck driver opening the pad: smooth mouse off, both pads none, both click pressures max.
    [InlineData(
        new byte[] { 0x87, 0x0F, 0x18, 0x00, 0x00, 0x07, 0x07, 0x00, 0x08, 0x07, 0x00, 0x34, 0xFF, 0xFF, 0x35, 0xFF, 0xFF },
        "ConsumerHeartbeat")]
    // A frame that says both: the IMU answer wins.
    [InlineData(new byte[] { 0x87, 0x06, 0x08, 0x07, 0x00, 0x30, 0x00, 0x00 }, "ImuOff")]
    public void TheImuModeTheResetsAndTheSdlWatchdogAreRead(byte[] frame, string expected)
    {
        Assert.Equal(Enum.Parse<MotionSignal>(expected), ViiperControllerBackend.ReadMotionSignal(frame));
    }

    [Theory]
    [InlineData(new byte[] { 0x87, 0x03, 0x09, 0x01, 0x00 })] // lizard mode only
    [InlineData(new byte[] { 0x87, 0x03, 0x08, 0x00, 0x00 })] // right trackpad set to a real mode by Steam
    [InlineData(new byte[] { 0x87, 0x02, 0x30, 0x01 })] // truncated triple
    [InlineData(new byte[] { 0xEB, 0x08, 0x00, 0x00, 0x00, 0x00 })] // rumble
    [InlineData(new byte[] { 0x81 })] // clear digital mappings, the watchdog's first half
    [InlineData(new byte[] { })]
    public void FramesThatSayNothingAboutMotionAreNone(byte[] frame)
    {
        Assert.Equal(MotionSignal.None, ViiperControllerBackend.ReadMotionSignal(frame));
    }
}
