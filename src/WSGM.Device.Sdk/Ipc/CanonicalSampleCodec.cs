using System;
using System.Buffers.Binary;
using WSGM.Device.Sdk.Input;

namespace WSGM.Device.Sdk.Ipc;

/// <summary>Fixed binary encoding for high-rate canonical controller and motion samples.</summary>
public static class CanonicalSampleCodec
{
    private const CanonicalButtons KnownButtons =
        (CanonicalButtons)((1U << 21) - 1);

    /// <summary>Exact payload size stored in each shared-memory ring slot.</summary>
    public const int PayloadBytes = 96;

    /// <summary>Current fixed-layout codec version.</summary>
    public const int Version = 1;

    /// <summary>Encodes a sample without allocation.</summary>
    /// <param name="sample">Canonical input sample.</param>
    /// <param name="destination">Destination of at least <see cref="PayloadBytes"/> bytes.</param>
    public static void Write(CanonicalControllerSample sample, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (destination.Length < PayloadBytes)
        {
            throw new ArgumentException($"Sample needs {PayloadBytes} bytes.", nameof(destination));
        }

        destination[..PayloadBytes].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(destination, Version);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], sample.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], sample.CycleGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], sample.Timestamp.UtcTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..], (uint)sample.Buttons);
        BinaryPrimitives.WriteInt32LittleEndian(destination[36..], (int)sample.Quality);
        WriteSingle(destination[40..], sample.LeftStickX);
        WriteSingle(destination[44..], sample.LeftStickY);
        WriteSingle(destination[48..], sample.RightStickX);
        WriteSingle(destination[52..], sample.RightStickY);
        WriteSingle(destination[56..], sample.LeftTrigger);
        WriteSingle(destination[60..], sample.RightTrigger);

        MotionSample? motion = sample.Motion;
        destination[64] = motion is null ? (byte)0 : (byte)1;
        destination[65] = motion?.HasGyro is true ? (byte)1 : (byte)0;
        destination[66] = motion?.HasAccelerometer is true ? (byte)1 : (byte)0;
        if (motion is not null)
        {
            WriteSingle(destination[68..], motion.GyroX);
            WriteSingle(destination[72..], motion.GyroY);
            WriteSingle(destination[76..], motion.GyroZ);
            WriteSingle(destination[80..], motion.AccelX);
            WriteSingle(destination[84..], motion.AccelY);
            WriteSingle(destination[88..], motion.AccelZ);
        }
    }

    /// <summary>Decodes and validates a fixed-layout sample.</summary>
    /// <param name="source">One ring-slot payload.</param>
    /// <param name="sample">Decoded sample when this method returns true.</param>
    /// <returns>Whether the version and all normalized ranges were valid.</returns>
    public static bool TryRead(ReadOnlySpan<byte> source, out CanonicalControllerSample? sample)
    {
        sample = null;
        if (source.Length < PayloadBytes || BinaryPrimitives.ReadInt32LittleEndian(source) != Version)
        {
            return false;
        }

        float leftX = ReadSingle(source[40..]);
        float leftY = ReadSingle(source[44..]);
        float rightX = ReadSingle(source[48..]);
        float rightY = ReadSingle(source[52..]);
        float leftTrigger = ReadSingle(source[56..]);
        float rightTrigger = ReadSingle(source[60..]);
        if (!IsAxis(leftX) || !IsAxis(leftY) || !IsAxis(rightX) || !IsAxis(rightY)
            || !IsTrigger(leftTrigger) || !IsTrigger(rightTrigger))
        {
            return false;
        }

        if (source[64] > 1 || source[65] > 1 || source[66] > 1)
        {
            return false;
        }

        long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(source[24..]);
        if (timestampTicks < DateTime.MinValue.Ticks || timestampTicks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        CanonicalButtons buttons =
            (CanonicalButtons)BinaryPrimitives.ReadUInt32LittleEndian(source[32..]);
        if ((buttons & ~KnownButtons) != 0)
        {
            return false;
        }

        bool hasMotion = source[64] == 1;
        float gyroX = ReadSingle(source[68..]);
        float gyroY = ReadSingle(source[72..]);
        float gyroZ = ReadSingle(source[76..]);
        float accelX = ReadSingle(source[80..]);
        float accelY = ReadSingle(source[84..]);
        float accelZ = ReadSingle(source[88..]);
        if (hasMotion && (!float.IsFinite(gyroX)
            || !float.IsFinite(gyroY)
            || !float.IsFinite(gyroZ)
            || !float.IsFinite(accelX)
            || !float.IsFinite(accelY)
            || !float.IsFinite(accelZ)))
        {
            return false;
        }

        sample = new CanonicalControllerSample
        {
            Sequence = BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
            CycleGeneration = BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
            Timestamp = new DateTimeOffset(timestampTicks, TimeSpan.Zero),
            Buttons = buttons,
            Quality = (SampleQuality)BinaryPrimitives.ReadInt32LittleEndian(source[36..]),
            LeftStickX = leftX,
            LeftStickY = leftY,
            RightStickX = rightX,
            RightStickY = rightY,
            LeftTrigger = leftTrigger,
            RightTrigger = rightTrigger,
            Motion = hasMotion ? new MotionSample
            {
                HasGyro = source[65] != 0,
                HasAccelerometer = source[66] != 0,
                GyroX = gyroX,
                GyroY = gyroY,
                GyroZ = gyroZ,
                AccelX = accelX,
                AccelY = accelY,
                AccelZ = accelZ,
            } : null,
        };
        return sample.Sequence >= 0 && sample.CycleGeneration >= 0
            && Enum.IsDefined(sample.Quality);
    }

    private static bool IsAxis(float value) => float.IsFinite(value) && value is >= -1.0f and <= 1.0f;

    private static bool IsTrigger(float value) => float.IsFinite(value) && value is >= 0.0f and <= 1.0f;

    private static void WriteSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
}
