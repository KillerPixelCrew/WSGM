using System.Buffers.Binary;
using System.Text.Json;
using WSGM.Device.Sdk;
using WSGM.Device.Sdk.Input;
using WSGM.Device.Sdk.Ipc;
using WSGM.Device.Sdk.Lifecycle;

namespace WSGM.Device.Tests;

public sealed class SdkTransportTests
{
    [Fact]
    public void FrameHeader_ExactApiVersion_WritesTheFixedLayout()
    {
        FrameHeader original = new()
        {
            PayloadLength = 23,
            ProtocolVersion = DeviceProtocol.Version,
            MessageType = DeviceMessageType.DiagnosticsSnapshot,
            RequestId = 42,
            Flags = FrameFlags.IsResponse,
        };
        byte[] bytes = new byte[FrameHeader.Size];

        original.WriteTo(bytes);

        Assert.Equal(DeviceApi.Version, DeviceProtocol.Version);
        Assert.Equal(original.PayloadLength, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(original.ProtocolVersion, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((ushort)original.MessageType, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(original.RequestId, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal((uint)original.Flags, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)));
    }

    [Fact]
    public async Task DeviceFrameStream_HostileLength_IsRejectedBeforeAllocation()
    {
        byte[] bytes = new byte[FrameHeader.Size];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, FrameHeader.MaxPayloadBytes + 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)DeviceMessageType.Command);
        await using ChunkedDuplexStream transport = new(bytes, maximumReadBytes: FrameHeader.Size);
        await using DeviceFrameStream frames = new(transport);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await frames.ReadAsync(CancellationToken.None));

        Assert.Contains("Rejected frame header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalSampleCodec_ValidStateRoundTripsAndNonFiniteMotionIsRejected()
    {
        CanonicalControllerSample original = new()
        {
            Sequence = 17,
            CycleGeneration = 4,
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(5),
            Buttons = CanonicalButtons.A | CanonicalButtons.RearPaddle2,
            LeftStickX = -0.5f,
            RightTrigger = 0.75f,
            Motion = new MotionSample { HasGyro = true, GyroX = 1.25f },
        };
        byte[] bytes = new byte[CanonicalSampleCodec.PayloadBytes];
        CanonicalSampleCodec.Write(original, bytes);

        Assert.True(CanonicalSampleCodec.TryRead(bytes, out CanonicalControllerSample? decoded));
        Assert.Equal(original, decoded);

        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(68),
            BitConverter.SingleToInt32Bits(float.NaN));
        Assert.False(CanonicalSampleCodec.TryRead(bytes, out _));
    }

    [Fact]
    public async Task DeviceFrameStream_PartialReads_ReturnOneCompleteBoundedFrame()
    {
        byte[] payload = "partial reads are normal"u8.ToArray();
        byte[] bytes = new byte[FrameHeader.Size + payload.Length];
        new FrameHeader
        {
            PayloadLength = payload.Length,
            ProtocolVersion = DeviceProtocol.Version,
            MessageType = DeviceMessageType.OperationAck,
            RequestId = 7,
        }.WriteTo(bytes);
        payload.CopyTo(bytes.AsSpan(FrameHeader.Size));
        await using ChunkedDuplexStream transport = new(bytes, maximumReadBytes: 3);
        await using DeviceFrameStream frames = new(transport);

        DeviceFrame? frame = await frames.ReadAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(7U, frame.Header.RequestId);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public void DiagnosticsJson_UsesTheCurrentSnapshotWithoutARetiredSchemaField()
    {
        DeviceDiagnosticsSnapshot snapshot = new()
        {
            PackageId = "wsgm.device.synthetic.dock-x1",
            DeviceId = "synthetic.dock-x1",
            CycleState = DeviceCycleState.Active,
            CycleGeneration = 9,
            CapturedAt = DateTimeOffset.UnixEpoch,
        };

        string json = JsonSerializer.Serialize(
            snapshot,
            DeviceWireJsonContext.Default.DeviceDiagnosticsSnapshot);

        Assert.DoesNotContain("schemaVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"cycleGeneration\":9", json, StringComparison.Ordinal);
    }

    private sealed class ChunkedDuplexStream(byte[] input, int maximumReadBytes) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, Math.Min(count, maximumReadBytes));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadBytes)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            _output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            await _input.DisposeAsync();
            await _output.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
