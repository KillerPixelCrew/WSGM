using System.Buffers.Binary;
using WSGM.Device.Contracts.Input;
using WSGM.Device.Contracts.Ipc;

namespace WSGM.DeviceHost.Tests;

public class CanonicalSampleCodecTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 28, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Write_ReviewedSample_UsesTheFrozenLittleEndianLayout()
    {
        CanonicalControllerSample sample = Sample();
        byte[] payload = new byte[CanonicalSampleCodec.PayloadBytes];

        CanonicalSampleCodec.Write(sample, payload);

        Assert.Equal(CanonicalSampleCodec.Version, BinaryPrimitives.ReadInt32LittleEndian(payload));
        Assert.Equal(17L, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(8)));
        Assert.Equal(9L, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(16)));
        Assert.Equal(Timestamp.UtcTicks, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(24)));
        Assert.Equal((uint)(CanonicalButtons.A | CanonicalButtons.RearPaddle2),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(32)));
        Assert.Equal(1, payload[64]);
        Assert.Equal(1, payload[65]);
        Assert.Equal(0, payload[66]);
        Assert.Equal(0, payload[67]);
        Assert.Equal(0, payload[92]);
        Assert.Equal(0, payload[95]);
    }

    [Fact]
    public void TryRead_ReviewedSample_RoundTripsCanonicalState()
    {
        CanonicalControllerSample original = Sample();
        byte[] payload = new byte[CanonicalSampleCodec.PayloadBytes];
        CanonicalSampleCodec.Write(original, payload);

        Assert.True(CanonicalSampleCodec.TryRead(payload, out CanonicalControllerSample? decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void TryRead_HostileTimestamp_ReturnsFalseInsteadOfThrowing()
    {
        byte[] payload = Payload();
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(24), long.MaxValue);

        Exception? exception = Record.Exception(() =>
            CanonicalSampleCodec.TryRead(payload, out _));

        Assert.Null(exception);
        Assert.False(CanonicalSampleCodec.TryRead(payload, out _));
    }

    [Fact]
    public void TryRead_NonCanonicalBooleanFlag_IsRejected()
    {
        byte[] payload = Payload();
        payload[64] = 2;

        Assert.False(CanonicalSampleCodec.TryRead(payload, out _));
    }

    [Fact]
    public void TryRead_NonFiniteMotionValue_IsRejected()
    {
        byte[] payload = Payload();
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(68),
            BitConverter.SingleToInt32Bits(float.NaN));

        Assert.False(CanonicalSampleCodec.TryRead(payload, out _));
    }

    private static byte[] Payload()
    {
        byte[] payload = new byte[CanonicalSampleCodec.PayloadBytes];
        CanonicalSampleCodec.Write(Sample(), payload);
        return payload;
    }

    private static CanonicalControllerSample Sample() => new()
    {
        Sequence = 17,
        DeviceGeneration = 9,
        Timestamp = Timestamp,
        Buttons = CanonicalButtons.A | CanonicalButtons.RearPaddle2,
        Quality = SampleQuality.Discontinuity,
        LeftStickX = -0.75f,
        LeftStickY = 0.5f,
        RightStickX = 0.25f,
        RightStickY = -0.125f,
        LeftTrigger = 0.2f,
        RightTrigger = 0.9f,
        Motion = new MotionSample
        {
            HasGyro = true,
            HasAccelerometer = false,
            GyroX = 1.25f,
            GyroY = -2.5f,
            GyroZ = 5.0f,
        },
    };
}
