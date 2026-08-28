using System.Text.Json;
using WSGM.Device.Contracts.Capabilities;
using WSGM.Device.Contracts.Ipc;

namespace WSGM.DeviceHost.Tests;

public class DeviceFrameStreamTests
{
    [Fact]
    public async Task ReadAsync_PartialTransportReads_ReturnsTheCompleteFrame()
    {
        byte[] payload = "partial reads are normal"u8.ToArray();
        byte[] bytes = Frame(payload);
        await using ChunkedDuplexStream transport = new(bytes, maxReadBytes: 3);
        await using DeviceFrameStream frames = new(transport);

        DeviceFrame? frame = await frames.ReadAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(DeviceMessageType.DiagnosticsSnapshot, frame.Header.MessageType);
        Assert.Equal(91U, frame.Header.RequestId);
        Assert.Equal(payload, frame.Payload);
    }

    [Fact]
    public async Task ReadAsync_PeerClosesDuringHeader_RejectsTheTruncatedFrame()
    {
        byte[] bytes = Frame("payload"u8.ToArray())[..(FrameHeader.Size - 1)];
        await using ChunkedDuplexStream transport = new(bytes, maxReadBytes: 2);
        await using DeviceFrameStream frames = new(transport);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => frames.ReadAsync(CancellationToken.None).AsTask());

        Assert.Equal("The peer closed during a frame header.", error.Message);
    }

    [Fact]
    public async Task ReadAsync_PeerClosesDuringPayload_RejectsTheTruncatedFrame()
    {
        byte[] bytes = Frame("payload"u8.ToArray());
        await using ChunkedDuplexStream transport = new(bytes[..^1], maxReadBytes: 4);
        await using DeviceFrameStream frames = new(transport);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => frames.ReadAsync(CancellationToken.None).AsTask());

        Assert.Equal("The peer closed during a frame payload.", error.Message);
    }

    [Fact]
    public async Task ReadAsync_BlockedTransport_CancellationIsObserved()
    {
        await using BlockingDuplexStream transport = new();
        await using DeviceFrameStream frames = new(transport);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => frames.ReadAsync(cancellation.Token).AsTask());
    }

    [Fact]
    public async Task HostWireSender_SourceGeneratedPayload_RoundTripsWithTheExpectedEnvelope()
    {
        await using ChunkedDuplexStream transport = new([], maxReadBytes: 8);
        await using DeviceFrameStream frames = new(transport);
        HostWireSender sender = new(frames);
        DeviceOperationAck original = new() { Completed = false, Detail = "bounded refusal" };

        await sender.SendAsync(
            DeviceMessageType.OperationAck,
            22,
            FrameFlags.IsResponse,
            original,
            DeviceWireJsonContext.Default.DeviceOperationAck,
            DeviceProtocol.MaxSupportedVersion,
            CancellationToken.None);

        byte[] bytes = transport.WrittenBytes;
        Assert.Equal(FrameError.None, FrameHeader.TryRead(bytes, out FrameHeader header));
        Assert.Equal(DeviceMessageType.OperationAck, header.MessageType);
        Assert.Equal(22U, header.RequestId);
        Assert.Equal(FrameFlags.IsResponse, header.Flags);
        DeviceOperationAck? decoded = JsonSerializer.Deserialize(
            bytes.AsSpan(FrameHeader.Size, header.PayloadLength),
            DeviceWireJsonContext.Default.DeviceOperationAck);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public async Task WriteAsync_CancelledBeforeAdmission_WritesNothing()
    {
        await using ChunkedDuplexStream transport = new([], maxReadBytes: 8);
        await using DeviceFrameStream frames = new(transport);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        FrameHeader header = Header(payloadLength: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => frames.WriteAsync(header, new byte[1], cancellation.Token).AsTask());

        Assert.Empty(transport.WrittenBytes);
    }

    [Fact]
    public async Task HostWireSender_StateDelta_PreservesTheProducerSequence()
    {
        await using ChunkedDuplexStream transport = new([], maxReadBytes: 8);
        await using DeviceFrameStream frames = new(transport);
        HostWireSender sender = new(frames);
        CapabilityStateDelta original = new(42, new CapabilityState
        {
            CapabilityId = "power.primary-limit",
            Available = true,
            Quality = HardwareStateQuality.Verified,
            DescriptorGeneration = 3,
            DeviceGeneration = 5,
            HostGeneration = 7,
        });

        await sender.SendAsync(
            DeviceMessageType.StateDelta,
            0,
            FrameFlags.None,
            original,
            DeviceWireJsonContext.Default.CapabilityStateDelta,
            DeviceProtocol.MaxSupportedVersion,
            CancellationToken.None);

        byte[] bytes = transport.WrittenBytes;
        Assert.Equal(FrameError.None, FrameHeader.TryRead(bytes, out FrameHeader header));
        CapabilityStateDelta? decoded = JsonSerializer.Deserialize(
            bytes.AsSpan(FrameHeader.Size, header.PayloadLength),
            DeviceWireJsonContext.Default.CapabilityStateDelta);
        Assert.Equal(42, decoded?.Sequence);
        Assert.Equal(original.State, decoded?.State);
    }

    private static byte[] Frame(byte[] payload)
    {
        byte[] bytes = new byte[FrameHeader.Size + payload.Length];
        Header(payload.Length).WriteTo(bytes);
        payload.CopyTo(bytes.AsSpan(FrameHeader.Size));
        return bytes;
    }

    private static FrameHeader Header(int payloadLength) => new()
    {
        PayloadLength = payloadLength,
        ProtocolVersion = DeviceProtocol.MaxSupportedVersion,
        MessageType = DeviceMessageType.DiagnosticsSnapshot,
        RequestId = 91,
    };

    private sealed class ChunkedDuplexStream(byte[] input, int maxReadBytes) : Stream
    {
        private readonly MemoryStream _input = new(input, writable: false);
        private readonly MemoryStream _output = new();

        public byte[] WrittenBytes => _output.ToArray();

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

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            _input.Read(buffer, offset, Math.Min(count, maxReadBytes));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer[..Math.Min(buffer.Length, maxReadBytes)], cancellationToken);

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

    private sealed class BlockingDuplexStream : Stream
    {
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

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
