using System;
using System.Buffers.Binary;
using WSGM.Device.Sdk.Input;

namespace WSGM.Input;

/// <summary>Packs a canonical sample into VIIPER's DualShock 4 input-state wire format.</summary>
internal static class DualShock4Report
{
    /// <summary>Length of one VIIPER DualShock 4 input state.</summary>
    internal const int Length = 31;

    private const ushort Square = 0x0010;
    private const ushort Cross = 0x0020;
    private const ushort Circle = 0x0040;
    private const ushort Triangle = 0x0080;
    private const ushort L1 = 0x0100;
    private const ushort R1 = 0x0200;
    private const ushort L2 = 0x0400;
    private const ushort R2 = 0x0800;
    private const ushort Share = 0x1000;
    private const ushort Options = 0x2000;
    private const ushort L3 = 0x4000;
    private const ushort R3 = 0x8000;
    private const ushort Ps = 0x0001;
    private const ushort TouchpadClick = 0x0002;

    private const byte DPadUp = 0x01;
    private const byte DPadDown = 0x02;
    private const byte DPadLeft = 0x04;
    private const byte DPadRight = 0x08;
    private const float GyroCountsPerDegreePerSecond = 16f;
    private const float AccelCountsPerG = 512f * 9.81f;
    private const ushort TouchMaxX = 1920;
    private const ushort TouchMaxY = 942;

    /// <summary>Writes one canonical sample into a DualShock 4 input state.</summary>
    internal static void Write(CanonicalControllerSample sample, Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException(
                $"A VIIPER DualShock 4 input state is exactly {Length} bytes.",
                nameof(destination));
        }

        destination.Clear();
        var buttons = sample.Buttons;
        destination[0] = unchecked((byte)Axis(sample.LeftStickX));
        destination[1] = unchecked((byte)Axis(-sample.LeftStickY));
        destination[2] = unchecked((byte)Axis(sample.RightStickX));
        destination[3] = unchecked((byte)Axis(-sample.RightStickY));

        var wireButtons = (ushort)(((buttons & CanonicalButtons.X) != 0 ? Square : 0)
                                   | ((buttons & CanonicalButtons.A) != 0 ? Cross : 0)
                                   | ((buttons & CanonicalButtons.B) != 0 ? Circle : 0)
                                   | ((buttons & CanonicalButtons.Y) != 0 ? Triangle : 0)
                                   | ((buttons & CanonicalButtons.LeftShoulder) != 0 ? L1 : 0)
                                   | ((buttons & CanonicalButtons.RightShoulder) != 0 ? R1 : 0)
                                   // The digital bit rises with the first analogue movement, as on a real DualShock 4.
                                   // A mid-travel threshold splits the press into two Steam Input activations; the same
                                   // split double-clicked and broke drags on the Deck target (device-observed 2026-09-02).
                                   | (sample.LeftTrigger > 0 ? L2 : 0)
                                   | (sample.RightTrigger > 0 ? R2 : 0)
                                   | ((buttons & CanonicalButtons.View) != 0 ? Share : 0)
                                   | ((buttons & CanonicalButtons.Menu) != 0 ? Options : 0)
                                   | ((buttons & CanonicalButtons.LeftStick) != 0 ? L3 : 0)
                                   | ((buttons & CanonicalButtons.RightStick) != 0 ? R3 : 0)
                                   | ((buttons & CanonicalButtons.Guide) != 0 ? Ps : 0)
                                   | ((buttons & (CanonicalButtons.LeftPadClick | CanonicalButtons.RightPadClick)) != 0
                                       ? TouchpadClick
                                       : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], wireButtons);

        destination[6] = (byte)(((buttons & CanonicalButtons.DPadUp) != 0 ? DPadUp : 0)
                                | ((buttons & CanonicalButtons.DPadDown) != 0 ? DPadDown : 0)
                                | ((buttons & CanonicalButtons.DPadLeft) != 0 ? DPadLeft : 0)
                                | ((buttons & CanonicalButtons.DPadRight) != 0 ? DPadRight : 0));
        destination[7] = WireScale.Trigger8(sample.LeftTrigger);
        destination[8] = WireScale.Trigger8(sample.RightTrigger);

        BinaryPrimitives.WriteUInt16LittleEndian(destination[9..11], Touch(sample.LeftPadX, TouchMaxX));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[11..13], Touch(-sample.LeftPadY, TouchMaxY));
        destination[13] = (buttons & CanonicalButtons.LeftPadTouch) != 0 ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..16], Touch(sample.RightPadX, TouchMaxX));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..18], Touch(-sample.RightPadY, TouchMaxY));
        destination[18] = (buttons & CanonicalButtons.RightPadTouch) != 0 ? (byte)1 : (byte)0;

        WriteMotion(sample.Motion, destination);
    }

    private static void WriteMotion(MotionSample? motion, Span<byte> destination)
    {
        if (motion is not { } sample)
        {
            return;
        }

        if (sample.HasGyro)
        {
            BinaryPrimitives.WriteInt16LittleEndian(destination[19..21],
                WireScale.Motion16(sample.GyroX, GyroCountsPerDegreePerSecond));
            BinaryPrimitives.WriteInt16LittleEndian(destination[21..23],
                WireScale.Motion16(sample.GyroY, GyroCountsPerDegreePerSecond));
            BinaryPrimitives.WriteInt16LittleEndian(destination[23..25],
                WireScale.Motion16(sample.GyroZ, GyroCountsPerDegreePerSecond));
        }

        if (!sample.HasAccelerometer)
        {
            return;
        }

        BinaryPrimitives.WriteInt16LittleEndian(destination[25..27],
            WireScale.Motion16(sample.AccelX, AccelCountsPerG));
        BinaryPrimitives.WriteInt16LittleEndian(destination[27..29],
            WireScale.Motion16(sample.AccelY, AccelCountsPerG));
        BinaryPrimitives.WriteInt16LittleEndian(destination[29..31],
            WireScale.Motion16(sample.AccelZ, AccelCountsPerG));
    }


    private static sbyte Axis(float value)
    {
        return (sbyte)Math.Clamp(MathF.Round(value * sbyte.MaxValue), -sbyte.MaxValue, sbyte.MaxValue);
    }


    private static ushort Touch(float value, ushort maximum)
    {
        return (ushort)Math.Clamp(
            MathF.Round((Math.Clamp(value, -1f, 1f) + 1f) / 2f * maximum),
            0,
            maximum);
    }
}
