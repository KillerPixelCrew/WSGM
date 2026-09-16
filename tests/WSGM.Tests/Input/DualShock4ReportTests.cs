using System.Buffers.Binary;
using WSGM.Device.Sdk.Input;
using WSGM.Input;
using static WSGM.Tests.Builders.ControllerSamples;

namespace WSGM.Tests.Input;

public sealed class DualShock4ReportTests
{
    [Fact]
    public void ButtonsUseTheDualShockLayoutAndBothPadClicksShareItsOneClick()
    {
        var frame = Frame(Sample(
            CanonicalButtons.A | CanonicalButtons.B | CanonicalButtons.X | CanonicalButtons.Y
            | CanonicalButtons.LeftShoulder | CanonicalButtons.RightShoulder
            | CanonicalButtons.View | CanonicalButtons.Menu
            | CanonicalButtons.LeftStick | CanonicalButtons.RightStick
            | CanonicalButtons.Guide | CanonicalButtons.RightPadClick));

        Assert.Equal(0xF3F3, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4..6)));
    }

    [Fact]
    public void DPadSticksAndTriggersUseDualShockCoordinates()
    {
        var frame = Frame(Sample(CanonicalButtons.DPadUp | CanonicalButtons.DPadRight) with
        {
            LeftStickX = -1f,
            LeftStickY = 1f,
            RightStickX = 0.5f,
            RightStickY = -0.5f,
            LeftTrigger = 0.2f,
            RightTrigger = 1f
        });

        Assert.Equal(unchecked((byte)-127), frame[0]);
        Assert.Equal(unchecked((byte)-127), frame[1]);
        Assert.Equal(64, frame[2]);
        Assert.Equal(64, frame[3]);
        Assert.Equal(0x09, frame[6]);
        Assert.Equal(51, frame[7]);
        Assert.Equal(byte.MaxValue, frame[8]);
        // Both digital trigger bits accompany their analogue values from the first movement,
        // as on a real DualShock 4.
        Assert.Equal(0x0C00, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4..6)) & 0x0C00);
    }

    [Fact]
    public void TwoCanonicalContactsMapOntoTheSingleTwoFingerTouchpad()
    {
        var frame = Frame(Sample(
            CanonicalButtons.LeftPadTouch | CanonicalButtons.RightPadTouch) with
        {
            LeftPadX = -1f,
            LeftPadY = 1f,
            RightPadX = 1f,
            RightPadY = -1f
        });

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(9..11)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11..13)));
        Assert.Equal(1, frame[13]);
        Assert.Equal(1920, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(14..16)));
        Assert.Equal(942, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(16..18)));
        Assert.Equal(1, frame[18]);
    }

    [Fact]
    public void MotionUsesViipersFixedPhysicalUnits()
    {
        var frame = Frame(Sample(CanonicalButtons.None) with
        {
            Motion = new MotionSample
            {
                HasGyro = true,
                GyroX = 100f,
                GyroY = -200f,
                GyroZ = 300f,
                HasAccelerometer = true,
                AccelX = 1f,
                AccelY = -0.5f,
                AccelZ = 0.25f
            }
        });

        Assert.Equal(1600, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(19..21)));
        Assert.Equal(-3200, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(21..23)));
        Assert.Equal(4800, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(23..25)));
        Assert.Equal(5023, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(25..27)));
        Assert.Equal(-2511, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(27..29)));
        Assert.Equal(1256, BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(29..31)));
    }

    [Fact]
    public void AbsentMotionIsNotInvented()
    {
        var frame = Frame(Sample(CanonicalButtons.None));

        Assert.Equal(new byte[12], frame[19..31]);
    }

    [Fact]
    public void AWrongSizedDestinationIsRefused() => Assert.Throws<ArgumentException>(() =>
        DualShock4Report.Write(Sample(CanonicalButtons.None), new byte[DualShock4Report.Length - 1]));

    private static byte[] Frame(CanonicalControllerSample sample)
    {
        var frame = new byte[DualShock4Report.Length];
        DualShock4Report.Write(sample, frame);
        return frame;
    }
}
