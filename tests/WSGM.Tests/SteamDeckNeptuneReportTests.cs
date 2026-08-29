using WSGM.Device.Sdk.Input;
using WSGM.Input;

namespace WSGM.Tests;

/// <summary>
/// The Steam Deck controller frame, as an executable specification.
/// </summary>
/// <remarks>
/// Every bit asserted here is agreed by three independent implementations — VIIPER's
/// <c>device/steamdeck/const.go</c>, HandheldCompanion's <c>SteamDeckTarget</c>, and <c>hhd</c>'s
/// virtual Steam Deck. The four rear controls and capacitive stick touch are the reason this target
/// can satisfy WSGM's controller contract at all, so they are pinned rather than left to inspection.
/// </remarks>
public sealed class SteamDeckNeptuneReportTests
{
    [Fact]
    public void AFrameIsAlwaysExactlySixtyFourBytes() =>
        Assert.Equal(64, SteamDeckNeptuneReport.Create(Sample(CanonicalButtons.None)).Length);

    [Fact]
    public void ANeutralSampleSetsNoButtonBitAnywhere()
    {
        byte[] frame = SteamDeckNeptuneReport.Create(Sample(CanonicalButtons.None));

        Assert.Equal(new byte[7], frame[8..15]);
    }

    [Theory]
    // Byte 8: face buttons and shoulders.
    [InlineData(CanonicalButtons.A, 8, 0x80)]
    [InlineData(CanonicalButtons.X, 8, 0x40)]
    [InlineData(CanonicalButtons.B, 8, 0x20)]
    [InlineData(CanonicalButtons.Y, 8, 0x10)]
    [InlineData(CanonicalButtons.LeftShoulder, 8, 0x08)]
    [InlineData(CanonicalButtons.RightShoulder, 8, 0x04)]
    // Byte 9: the lower-left paddle, menu cluster, and d-pad.
    [InlineData(CanonicalButtons.RearPaddle3, 9, 0x80)]
    [InlineData(CanonicalButtons.Menu, 9, 0x40)]
    [InlineData(CanonicalButtons.Guide, 9, 0x20)]
    [InlineData(CanonicalButtons.View, 9, 0x10)]
    [InlineData(CanonicalButtons.DPadDown, 9, 0x08)]
    [InlineData(CanonicalButtons.DPadLeft, 9, 0x04)]
    [InlineData(CanonicalButtons.DPadRight, 9, 0x02)]
    [InlineData(CanonicalButtons.DPadUp, 9, 0x01)]
    // Byte 10: left stick click, trackpads, lower-right paddle.
    [InlineData(CanonicalButtons.LeftStick, 10, 0x40)]
    [InlineData(CanonicalButtons.RightPadTouch, 10, 0x10)]
    [InlineData(CanonicalButtons.LeftPadTouch, 10, 0x08)]
    [InlineData(CanonicalButtons.RightPadClick, 10, 0x04)]
    [InlineData(CanonicalButtons.LeftPadClick, 10, 0x02)]
    [InlineData(CanonicalButtons.RearPaddle4, 10, 0x01)]
    // Byte 11: right stick click.
    [InlineData(CanonicalButtons.RightStick, 11, 0x04)]
    // Byte 13: stick touch and the two upper paddles.
    [InlineData(CanonicalButtons.RightStickTouch, 13, 0x80)]
    [InlineData(CanonicalButtons.LeftStickTouch, 13, 0x40)]
    [InlineData(CanonicalButtons.RearPaddle2, 13, 0x04)]
    [InlineData(CanonicalButtons.RearPaddle1, 13, 0x02)]
    // Byte 14: quick access.
    [InlineData(CanonicalButtons.QuickAccess, 14, 0x04)]
    public void EachControlLandsOnItsAgreedBit(CanonicalButtons button, int index, int bit)
    {
        byte[] frame = SteamDeckNeptuneReport.Create(Sample(button));

        Assert.Equal((byte)bit, frame[index]);
    }

    [Fact]
    public void AllFourRearControlsAreDistinctAndSimultaneous()
    {
        byte[] frame = SteamDeckNeptuneReport.Create(Sample(
            CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2
            | CanonicalButtons.RearPaddle3 | CanonicalButtons.RearPaddle4));

        // Upper pair on byte 13, lower pair split across bytes 9 and 10 — the layout that made the
        // alternative backend unable to carry more than two of them.
        Assert.Equal(0x06, frame[13]);
        Assert.Equal(0x80, frame[9]);
        Assert.Equal(0x01, frame[10]);
    }

    [Fact]
    public void BothStickTouchSensorsAreCarried()
    {
        byte[] frame = SteamDeckNeptuneReport.Create(Sample(
            CanonicalButtons.LeftStickTouch | CanonicalButtons.RightStickTouch));

        Assert.Equal(0xC0, frame[13]);
    }

    [Fact]
    public void AnAnaloguePullAlsoSetsTheDigitalTriggerEdge()
    {
        byte[] frame = SteamDeckNeptuneReport.Create(
            Sample(CanonicalButtons.None) with { LeftTrigger = 0.5f, RightTrigger = 1f });

        Assert.Equal(0x03, frame[8]);
        Assert.Equal(32768, BitConverter.ToUInt16(frame, 44));
        Assert.Equal(ushort.MaxValue, BitConverter.ToUInt16(frame, 46));
    }

    [Fact]
    public void StickAxesScaleOntoTheSignedWireRange()
    {
        byte[] frame = SteamDeckNeptuneReport.Create(Sample(CanonicalButtons.None) with
        {
            LeftStickX = 1f,
            LeftStickY = -1f,
            RightStickX = 0f,
            RightStickY = 0.5f,
        });

        Assert.Equal(short.MaxValue, BitConverter.ToInt16(frame, 48));
        // One short of the negative extreme: SDL3 negates stick Y with a plain unary minus, so
        // -32768 wraps back to itself and a fully-down stick would read as fully up.
        Assert.Equal(short.MinValue + 1, BitConverter.ToInt16(frame, 50));
        Assert.Equal(0, BitConverter.ToInt16(frame, 52));
        Assert.Equal(16384, BitConverter.ToInt16(frame, 54));
    }

    [Fact]
    public void TouchContactsAndForcesAreCarried()
    {
        byte[] frame = SteamDeckNeptuneReport.Create(Sample(CanonicalButtons.None) with
        {
            LeftPadX = 1f,
            LeftPadY = -1f,
            RightPadX = 0.5f,
            RightPadY = 0f,
            LeftPadForce = 1f,
            RightPadForce = 0.5f,
            LeftStickForce = 1f,
            RightStickForce = 0f,
        });

        Assert.Equal(short.MaxValue, BitConverter.ToInt16(frame, 16));
        Assert.Equal(short.MinValue + 1, BitConverter.ToInt16(frame, 18));
        Assert.Equal(16384, BitConverter.ToInt16(frame, 20));
        Assert.Equal(0, BitConverter.ToInt16(frame, 22));
        Assert.Equal(ushort.MaxValue, BitConverter.ToUInt16(frame, 56));
        Assert.Equal(32768, BitConverter.ToUInt16(frame, 58));
        Assert.Equal(ushort.MaxValue, BitConverter.ToUInt16(frame, 60));
        Assert.Equal(0, BitConverter.ToUInt16(frame, 62));
    }

    [Fact]
    public void MotionIsCarriedOnlyForTheSensorsTheDeviceHas()
    {
        byte[] gyroOnly = SteamDeckNeptuneReport.Create(Sample(CanonicalButtons.None) with
        {
            Motion = new MotionSample
            {
                HasGyro = true,
                GyroX = 100,
                GyroY = 200,
                GyroZ = 300,
                AccelX = 999,
            },
        });

        Assert.Equal(100, BitConverter.ToInt16(gyroOnly, 30));
        Assert.Equal(200, BitConverter.ToInt16(gyroOnly, 32));
        Assert.Equal(300, BitConverter.ToInt16(gyroOnly, 34));
        // The accelerometer was not declared, so its bytes stay zero rather than carrying a value
        // the device never reported.
        Assert.Equal(0, BitConverter.ToInt16(gyroOnly, 24));
    }

    [Fact]
    public void TheOrientationQuaternionIsNeverPopulated()
    {
        byte[] frame = SteamDeckNeptuneReport.Create(Sample(CanonicalButtons.None) with
        {
            Motion = new MotionSample { HasGyro = true, HasAccelerometer = true, GyroX = 1 },
        });

        // A frozen identity quaternion makes Steam ignore raw angular velocity and collapse
        // gyro-to-stick to centre, so WSGM sends raw motion and no orientation at all.
        Assert.Equal(new byte[8], frame[36..44]);
    }

    [Fact]
    public void TheTargetDropsNothingTheCanonicalModelDefines()
    {
        CanonicalButtons everything = SteamDeckNeptuneReport.Supported;

        Assert.Equal(everything, VirtualTargetProfile.SteamDeck.Consume(everything));
    }

    [Fact]
    public void AWrongSizedDestinationIsRefused()
    {
        byte[] tooSmall = new byte[32];

        Assert.Throws<ArgumentException>(() =>
            SteamDeckNeptuneReport.Write(Sample(CanonicalButtons.None), tooSmall));
    }

    private static CanonicalControllerSample Sample(CanonicalButtons buttons) => new()
    {
        Sequence = 1,
        CycleGeneration = 1,
        Timestamp = DateTimeOffset.UtcNow,
        Buttons = buttons,
    };
}
