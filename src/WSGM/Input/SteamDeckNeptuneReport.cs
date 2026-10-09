using System;
using System.Buffers.Binary;
using CanonicalButtons = LibHandheld.Contracts.CanonicalButtons;
using CanonicalControllerSample = LibHandheld.Contracts.CanonicalControllerSample;
using MotionSample = LibHandheld.Contracts.MotionSample;

namespace WSGM.Input;

/// <summary>
///     Packs a canonical sample into the Steam Deck's 64-byte controller frame.
/// </summary>
/// <remarks>
///     This is the wire format WSGM hands to VIIPER, which unmarshals it and re-emits it to the host as
///     the real device's <c>ID_CONTROLLER_DECK_STATE</c> report. VIIPER's pinned packet definition and
///     SDL's Steam Deck driver provide the byte layout, axis decode, and physical scales used here.
/// </remarks>
internal static class SteamDeckNeptuneReport
{
    /// <summary>Length of one Steam Deck controller frame.</summary>
    internal const int Length = 64;

    // Byte 8: face buttons, shoulders, and the digital edge of the triggers.
    private const byte Byte8A = 0x80;
    private const byte Byte8X = 0x40;
    private const byte Byte8B = 0x20;
    private const byte Byte8Y = 0x10;
    private const byte Byte8L1 = 0x08;
    private const byte Byte8R1 = 0x04;
    private const byte Byte8L2 = 0x02;
    private const byte Byte8R2 = 0x01;

    // Byte 9: the lower-left paddle, the menu cluster, and the d-pad.
    private const byte Byte9L5 = 0x80;
    private const byte Byte9Menu = 0x40;
    private const byte Byte9Steam = 0x20;
    private const byte Byte9Options = 0x10;
    private const byte Byte9DPadDown = 0x08;
    private const byte Byte9DPadLeft = 0x04;
    private const byte Byte9DPadRight = 0x02;
    private const byte Byte9DPadUp = 0x01;

    // Byte 10: left stick click, trackpad touch and click, and the lower-right paddle.
    private const byte Byte10L3 = 0x40;
    private const byte Byte10RPadTouch = 0x10;
    private const byte Byte10LPadTouch = 0x08;
    private const byte Byte10RPadPress = 0x04;
    private const byte Byte10LPadPress = 0x02;
    private const byte Byte10R5 = 0x01;

    // Byte 11: right stick click.
    private const byte Byte11R3 = 0x04;

    // Byte 13: capacitive stick touch and the two upper paddles.
    private const byte Byte13RStickTouch = 0x80;
    private const byte Byte13LStickTouch = 0x40;
    private const byte Byte13R4 = 0x04;
    private const byte Byte13L4 = 0x02;

    // Byte 14: the quick-access button.
    private const byte Byte14QuickAccess = 0x04;

    /// <summary>
    ///     The travel at which the digital trigger bit rises. Steam reads that bit as Full Pull and
    ///     the analogue value as Soft Pull, and nothing else produces Full Pull: with the bits clear
    ///     it never fired (Xbox Ally X, 2026-09-27). HHD's Deck emulation raises the bit at 0.8 for
    ///     every pad without a trigger click, the ROG Ally included (<c>trigger_discrete_lvl</c>).
    /// </summary>
    private const float DigitalTriggerTravel = 0.8f;

    // The Deck IMU fields are signed 16-bit values over fixed physical ranges. Steam/SDL expose
    // their application-space axes as raw X, raw Z, -raw Y, so WSGM reverses that transform while
    // packing the canonical application-space sample.
    private const float GyroCountsPerDegreePerSecond = 16f;
    private const float AccelCountsPerG = 16384f;

    /// <summary>
    ///     Writes one canonical sample into a Steam Deck frame.
    /// </summary>
    /// <param name="sample">The canonical sample to send.</param>
    /// <param name="destination">A buffer of exactly <see cref="Length" /> bytes.</param>
    /// <exception cref="ArgumentException">The destination is the wrong length.</exception>
    internal static void Write(CanonicalControllerSample sample, Span<byte> destination)
    {
        if (destination.Length != Length)
        {
            throw new ArgumentException(
                $"A Steam Deck frame is exactly {Length} bytes.",
                nameof(destination));
        }

        destination.Clear();

        // Byte 0 non-zero tells the decoder the frame carries its own counter. VIIPER stamps the
        // header and the packet number itself when it re-emits, so WSGM leaves the counter alone
        // rather than inventing a sequence the device would then contradict.
        var buttons = sample.Buttons;
        destination[8] = (byte)(((buttons & CanonicalButtons.A) != 0 ? Byte8A : 0)
                                | ((buttons & CanonicalButtons.X) != 0 ? Byte8X : 0)
                                | ((buttons & CanonicalButtons.B) != 0 ? Byte8B : 0)
                                | ((buttons & CanonicalButtons.Y) != 0 ? Byte8Y : 0)
                                | ((buttons & CanonicalButtons.LeftShoulder) != 0 ? Byte8L1 : 0)
                                | ((buttons & CanonicalButtons.RightShoulder) != 0 ? Byte8R1 : 0)
                                // Raising the bit with the first movement, as Handheld Companion's
                                // Deck target does, fired Full Pull before Soft Pull and made every
                                // hip-fire style take the full-pull action; leaving it clear made
                                // Full Pull never fire (Xbox Ally X, 2026-09-27). The 2026-09-02
                                // desktop double-click that was blamed on a mid-travel threshold
                                // came from the 0..65535 trigger scale fixed the same day.
                                | (sample.LeftTrigger > DigitalTriggerTravel ? Byte8L2 : 0)
                                | (sample.RightTrigger > DigitalTriggerTravel ? Byte8R2 : 0));

        destination[9] = (byte)(((buttons & CanonicalButtons.RearPaddle3) != 0 ? Byte9L5 : 0)
                                | ((buttons & CanonicalButtons.Menu) != 0 ? Byte9Menu : 0)
                                | ((buttons & CanonicalButtons.Guide) != 0 ? Byte9Steam : 0)
                                | ((buttons & CanonicalButtons.View) != 0 ? Byte9Options : 0)
                                | ((buttons & CanonicalButtons.DPadDown) != 0 ? Byte9DPadDown : 0)
                                | ((buttons & CanonicalButtons.DPadLeft) != 0 ? Byte9DPadLeft : 0)
                                | ((buttons & CanonicalButtons.DPadRight) != 0 ? Byte9DPadRight : 0)
                                | ((buttons & CanonicalButtons.DPadUp) != 0 ? Byte9DPadUp : 0));

        destination[10] = (byte)(((buttons & CanonicalButtons.LeftStick) != 0 ? Byte10L3 : 0)
                                 | ((buttons & CanonicalButtons.RightPadTouch) != 0 ? Byte10RPadTouch : 0)
                                 | ((buttons & CanonicalButtons.LeftPadTouch) != 0 ? Byte10LPadTouch : 0)
                                 | ((buttons & CanonicalButtons.RightPadClick) != 0 ? Byte10RPadPress : 0)
                                 | ((buttons & CanonicalButtons.LeftPadClick) != 0 ? Byte10LPadPress : 0)
                                 | ((buttons & CanonicalButtons.RearPaddle4) != 0 ? Byte10R5 : 0));

        destination[11] = (byte)((buttons & CanonicalButtons.RightStick) != 0 ? Byte11R3 : 0);

        destination[13] = (byte)(((buttons & CanonicalButtons.RightStickTouch) != 0 ? Byte13RStickTouch : 0)
                                 | ((buttons & CanonicalButtons.LeftStickTouch) != 0 ? Byte13LStickTouch : 0)
                                 | ((buttons & CanonicalButtons.RearPaddle2) != 0 ? Byte13R4 : 0)
                                 | ((buttons & CanonicalButtons.RearPaddle1) != 0 ? Byte13L4 : 0));

        destination[14] = (byte)((buttons & CanonicalButtons.QuickAccess) != 0 ? Byte14QuickAccess : 0);

        BinaryPrimitives.WriteInt16LittleEndian(destination[16..18], WireScale.Axis16(sample.LeftPadX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[18..20], WireScale.Axis16(sample.LeftPadY));
        BinaryPrimitives.WriteInt16LittleEndian(destination[20..22], WireScale.Axis16(sample.RightPadX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[22..24], WireScale.Axis16(sample.RightPadY));

        WriteMotion(sample.Motion, destination);

        // Triggers are signed 16-bit on the wire; the canonical model is a 0..1 unit.
        BinaryPrimitives.WriteUInt16LittleEndian(destination[44..46], Trigger(sample.LeftTrigger));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[46..48], Trigger(sample.RightTrigger));

        BinaryPrimitives.WriteInt16LittleEndian(destination[48..50], WireScale.Axis16(sample.LeftStickX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[50..52], WireScale.Axis16(sample.LeftStickY));
        BinaryPrimitives.WriteInt16LittleEndian(destination[52..54], WireScale.Axis16(sample.RightStickX));
        BinaryPrimitives.WriteInt16LittleEndian(destination[54..56], WireScale.Axis16(sample.RightStickY));

        BinaryPrimitives.WriteUInt16LittleEndian(destination[56..58], Trigger(sample.LeftPadForce));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[58..60], Trigger(sample.RightPadForce));
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[60..62],
            Trigger(sample.LeftStickForce));
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[62..64],
            Trigger(sample.RightStickForce));
    }

    private static void WriteMotion(MotionSample? motion, Span<byte> destination)
    {
        if (motion is not { } sample)
        {
            return;
        }

        if (sample.HasAccelerometer)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[24..26],
                WireScale.Motion16(sample.AccelX, AccelCountsPerG));
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[26..28],
                WireScale.Motion16(-sample.AccelZ, AccelCountsPerG));
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[28..30],
                WireScale.Motion16(sample.AccelY, AccelCountsPerG));
        }

        // The orientation quaternion at bytes 36..44 stays zero on purpose. WSGM publishes raw
        // angular velocity and never computes an orientation, and a frozen identity quaternion
        // makes Steam ignore the raw gyro and collapse gyro-to-stick to centre.
        if (!sample.HasGyro)
        {
            return;
        }

        BinaryPrimitives.WriteInt16LittleEndian(
            destination[30..32],
            WireScale.Motion16(sample.GyroX, GyroCountsPerDegreePerSecond));
        BinaryPrimitives.WriteInt16LittleEndian(
            destination[32..34],
            WireScale.Motion16(-sample.GyroZ, GyroCountsPerDegreePerSecond));
        BinaryPrimitives.WriteInt16LittleEndian(
            destination[34..36],
            WireScale.Motion16(sample.GyroY, GyroCountsPerDegreePerSecond));
    }


    /// <summary>Scales a 0..1 unit onto the wire's trigger/pressure range.</summary>
    /// <remarks>
    ///     The trigger and pressure fields are signed 16-bit on the wire (Valve's
    ///     <c>sTriggerRaw</c>/<c>sPressure</c> members; SDL3 doubles 0..32767 onto the full axis
    ///     range), so full travel is 32767. Scaling to 65535 made every pull past half travel read
    ///     as negative — Steam saw the trigger release mid-pull and press again on the way back,
    ///     which double-clicked and tore held drags loose in desktop mode (device-observed
    ///     2026-09-02).
    /// </remarks>
    private static ushort Trigger(float value)
    {
        return (ushort)Math.Clamp(MathF.Round(value * short.MaxValue), 0, short.MaxValue);
    }
}
